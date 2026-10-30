using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace McpToolkit.Shell;

public sealed partial class ShellCommandRunner(IOptions<ShellOptions> options)
{
    public async Task<ShellCommandResult> RunAsync(
        string command,
        ShellKind requestedShell,
        string? workingDirectoryOverride,
        int? timeoutSecondsOverride,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("O parâmetro 'command' não pode ser vazio.");

        EnsureCommandAllowed(command, settings);

        var workingDirectory = ResolveWorkingDirectory(workingDirectoryOverride, settings);
        var effectiveTimeoutSeconds = ResolveTimeoutSeconds(timeoutSecondsOverride, settings);

        var (fileName, arguments, effectiveShell) = ShellResolver.Resolve(requestedShell, settings.DefaultShell, command);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory
        };
        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);

        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        var outTruncated = false;
        var errTruncated = false;
        var outLock = new object();
        var errLock = new object();
        // Teto de segurança contra saída ilimitada (ex: "yes" ou "find /") acumulando GB em memória
        // durante o timeout inteiro — a truncagem final continua cortando no tamanho exato configurado.
        var maxAccumulateChars = settings.MaxOutputChars > 0 ? settings.MaxOutputChars : int.MaxValue;
        var timedOut = false;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (outLock)
            {
                if (stdOut.Length < maxAccumulateChars) stdOut.AppendLine(e.Data);
                else outTruncated = true;
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (errLock)
            {
                if (stdErr.Length < maxAccumulateChars) stdErr.AppendLine(e.Data);
                else errTruncated = true;
            }
        };

        var stopwatch = Stopwatch.StartNew();

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Falha ao iniciar o shell '{fileName}': {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(effectiveTimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Sempre mata o processo, seja o timeout ou o cancelamento do chamador que disparou —
            // a condição anterior só matava no caso de timeout, deixando o processo órfão quando
            // era o cliente MCP que cancelava a chamada.
            timedOut = !cancellationToken.IsCancellationRequested;
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
                throw;
        }

        stopwatch.Stop();
        // Limitado no tempo: um neto de processo que herdou os handles dos pipes poderia manter
        // o WaitForExit() sem parâmetro bloqueado para sempre.
        process.WaitForExit(5000);

        lock (outLock) lock (errLock)
        {
            var (truncatedOut, outWasTruncated) = Truncate(stdOut.ToString(), settings.MaxOutputChars);
            var (truncatedErr, errWasTruncated) = Truncate(stdErr.ToString(), settings.MaxOutputChars);

            return new ShellCommandResult(
                ExitCode: timedOut ? -1 : process.ExitCode,
                StdOut: truncatedOut,
                StdErr: truncatedErr,
                TimedOut: timedOut,
                Truncated: outTruncated || errTruncated || outWasTruncated || errWasTruncated,
                DurationMs: stopwatch.ElapsedMilliseconds,
                Shell: effectiveShell.ToString(),
                WorkingDirectory: workingDirectory);
        }
    }

    // Divide o comando em segmentos por ; && || | & (e por quebra de linha — "echo ok\nwhoami" é
    // um único segmento sem isso, e seu primeiro token "echo" passava ileso pelo allow/blocklist)
    // para checar allow/blocklist em cada um. Isso reduz o acidente óbvio (ex: "echo ok && rm -rf x"),
    // mas não é uma sandbox: quoting, subshells e comandos como "powershell -c ..." ainda contornam a checagem.
    [GeneratedRegex(@"&&|\|\||[;&|\r\n]")]
    private static partial Regex CommandSeparatorRegex();

    private static void EnsureCommandAllowed(string command, ShellOptions settings)
    {
        if (settings.AllowedCommands.Length == 0 && settings.BlockedCommands.Length == 0)
            return;

        var segments = CommandSeparatorRegex().Split(command);
        foreach (var segment in segments)
        {
            var firstToken = segment.TrimStart().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (firstToken is null)
                continue;

            var commandName = Path.GetFileNameWithoutExtension(firstToken);

            if (settings.BlockedCommands.Length > 0 &&
                settings.BlockedCommands.Any(blocked => string.Equals(blocked, commandName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"O comando '{commandName}' está bloqueado pela configuração deste servidor.");

            if (settings.AllowedCommands.Length > 0 &&
                !settings.AllowedCommands.Any(allowed => string.Equals(allowed, commandName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"O comando '{commandName}' não está na lista de comandos permitidos deste servidor.");
        }
    }

    private static string ResolveWorkingDirectory(string? workingDirectoryOverride, ShellOptions settings)
    {
        var workingDirectory = !string.IsNullOrWhiteSpace(workingDirectoryOverride)
            ? workingDirectoryOverride
            : settings.WorkingDirectory;

        if (string.IsNullOrWhiteSpace(workingDirectory))
            return Environment.CurrentDirectory;

        if (!Directory.Exists(workingDirectory))
            throw new InvalidOperationException($"O diretório de trabalho '{workingDirectory}' não existe.");

        return workingDirectory;
    }

    private static int ResolveTimeoutSeconds(int? timeoutSecondsOverride, ShellOptions settings)
    {
        var ceiling = settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 60;

        if (timeoutSecondsOverride is null or <= 0)
            return ceiling;

        return Math.Min(timeoutSecondsOverride.Value, ceiling);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Processo pode já ter saído entre a checagem e o kill.
        }
    }

    private static (string Text, bool Truncated) Truncate(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars)
            return (text, false);

        return (text[..maxChars], true);
    }
}
