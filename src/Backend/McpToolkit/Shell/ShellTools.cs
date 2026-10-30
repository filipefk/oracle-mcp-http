using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace McpToolkit.Shell;

[McpServerToolType]
public sealed class ShellTools(IOptions<ShellOptions> options, ILogger<ShellTools> logger, ShellCommandRunner runner)
{
    [McpServerTool(Title = "Executar comando de shell", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true), Description("Executa um comando no shell do sistema operacional onde este servidor MCP está rodando (não na máquina do usuário) — PowerShell/cmd no Windows, bash/sh no Linux e macOS. Chame get_shell_info antes para saber qual shell está disponível e se uma senha é exigida. Nunca lança erro por causa de exit code diferente de zero — isso é um resultado legítimo, retornado em exitCode/stdErr.")]
    public async Task<string> execute_shell_command(
        [Description("Comando a ser executado no shell.")] string command,
        [Description("Shell a ser usado: auto, powershell, cmd, bash ou sh. Se omitido ou 'auto', usa o shell padrão configurado no servidor (ou o padrão do sistema operacional).")] string shell = "auto",
        [Description("Diretório de trabalho para executar o comando. Se omitido, usa o diretório padrão configurado no servidor.")] string? working_directory = null,
        [Description("Tempo máximo em segundos para o comando executar antes de ser cancelado. Sujeito a um teto configurado no servidor.")] int? timeout_seconds = null,
        [Description("Senha exigida apenas se o servidor estiver configurado com uma. Consulte get_shell_info para saber se é necessária.")] string? password = null,
        CancellationToken ct = default)
    {
        EnsureAuthorized(password);

        var shellKind = ParseShellKind(shell);

        logger.LogInformation(
            "Executando comando de shell (shell solicitado: {RequestedShell}, workingDirectory: {WorkingDirectory})",
            shellKind, working_directory ?? options.Value.WorkingDirectory);

        var result = await runner.RunAsync(command, shellKind, working_directory, timeout_seconds, ct);

        return new JsonObject
        {
            ["exitCode"] = result.ExitCode,
            ["stdOut"] = result.StdOut,
            ["stdErr"] = result.StdErr,
            ["timedOut"] = result.TimedOut,
            ["truncated"] = result.Truncated,
            ["durationMs"] = result.DurationMs,
            ["shell"] = result.Shell,
            ["workingDirectory"] = result.WorkingDirectory
        }.ToJsonString();
    }

    [McpServerTool(Title = "Obter informações do shell", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Retorna informações sobre o shell disponível neste servidor: sistema operacional, shell padrão, shells disponíveis, diretório de trabalho e limites configurados. Não exige senha.")]
    public string get_shell_info()
    {
        var settings = options.Value;

        var operatingSystem = OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsMacOS() ? "macOS"
            : OperatingSystem.IsLinux() ? "Linux"
            : "Desconhecido";

        var defaultShell = settings.DefaultShell != ShellKind.Auto
            ? settings.DefaultShell
            : ShellResolver.ResolveDefaultKind();

        var availableShells = new JsonArray([.. ShellResolver.GetAvailableShells()
            .Select(s => (JsonNode)new JsonObject
            {
                ["shell"] = s.Kind.ToString(),
                ["available"] = s.Available
            })]);

        return new JsonObject
        {
            ["operatingSystem"] = operatingSystem,
            ["defaultShell"] = defaultShell.ToString(),
            ["availableShells"] = availableShells,
            ["workingDirectory"] = string.IsNullOrWhiteSpace(settings.WorkingDirectory)
                ? Environment.CurrentDirectory
                : settings.WorkingDirectory,
            ["timeoutSeconds"] = settings.TimeoutSeconds,
            ["maxOutputChars"] = settings.MaxOutputChars,
            ["requiresPassword"] = !string.IsNullOrEmpty(settings.Password)
        }.ToJsonString();
    }

    private static ShellKind ParseShellKind(string shell)
    {
        if (Enum.TryParse<ShellKind>(shell, ignoreCase: true, out var parsed))
            return parsed;

        throw new InvalidOperationException(
            $"O parâmetro 'shell' contém um valor inválido: '{shell}'. Valores aceitos: auto, powershell, cmd, bash, sh.");
    }

    private void EnsureAuthorized(string? password)
    {
        var configuredPassword = options.Value.Password;
        if (string.IsNullOrEmpty(configuredPassword))
            return;

        var providedBytes = Encoding.UTF8.GetBytes(password ?? "");
        var configuredBytes = Encoding.UTF8.GetBytes(configuredPassword);

        var isValid = providedBytes.Length == configuredBytes.Length &&
            CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes);

        if (!isValid)
            throw new InvalidOperationException("Senha obrigatória ou incorreta para executar comandos de shell.");
    }
}
