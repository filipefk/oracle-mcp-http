namespace McpToolkit.Shell;

public static class ShellResolver
{
    public static ShellKind ResolveDefaultKind()
    {
        if (OperatingSystem.IsWindows())
            return ShellKind.PowerShell;

        return File.Exists("/bin/bash") || FindOnPath("bash") is not null
            ? ShellKind.Bash
            : ShellKind.Sh;
    }

    public static (string FileName, IReadOnlyList<string> Arguments, ShellKind Effective) Resolve(ShellKind requested, ShellKind configuredDefault, string command)
    {
        var effective = requested != ShellKind.Auto
            ? requested
            : configuredDefault != ShellKind.Auto
                ? configuredDefault
                : ResolveDefaultKind();

        return effective switch
        {
            ShellKind.PowerShell => ResolvePowerShell(command),
            ShellKind.Cmd => ResolveCmd(command),
            ShellKind.Bash => ResolveBash(command),
            ShellKind.Sh => ResolveSh(command),
            _ => throw new InvalidOperationException($"Shell '{effective}' não é suportado.")
        };
    }

    public static IReadOnlyList<(ShellKind Kind, bool Available)> GetAvailableShells()
    {
        if (OperatingSystem.IsWindows())
            return
            [
                (ShellKind.PowerShell, true),
                (ShellKind.Cmd, true)
            ];

        return
        [
            (ShellKind.Bash, File.Exists("/bin/bash") || FindOnPath("bash") is not null),
            (ShellKind.Sh, File.Exists("/bin/sh") || FindOnPath("sh") is not null)
        ];
    }

    private static (string, IReadOnlyList<string>, ShellKind) ResolvePowerShell(string command)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("O shell 'PowerShell' só está disponível no Windows.");

        var fileName = FindOnPath("pwsh") ?? "powershell.exe";
        var fullCommand = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + command;
        return (fileName, ["-NoProfile", "-NonInteractive", "-Command", fullCommand], ShellKind.PowerShell);
    }

    private static (string, IReadOnlyList<string>, ShellKind) ResolveCmd(string command)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("O shell 'Cmd' só está disponível no Windows.");

        var fullCommand = "chcp 65001>nul & " + command;
        return ("cmd.exe", ["/c", fullCommand], ShellKind.Cmd);
    }

    private static (string, IReadOnlyList<string>, ShellKind) ResolveBash(string command)
    {
        if (OperatingSystem.IsWindows())
            throw new InvalidOperationException("O shell 'Bash' não está disponível neste servidor Windows.");

        var fileName = File.Exists("/bin/bash") ? "/bin/bash" : FindOnPath("bash")
            ?? throw new InvalidOperationException("Bash não encontrado neste sistema.");
        return (fileName, ["-c", command], ShellKind.Bash);
    }

    private static (string, IReadOnlyList<string>, ShellKind) ResolveSh(string command)
    {
        if (OperatingSystem.IsWindows())
            throw new InvalidOperationException("O shell 'Sh' não está disponível neste servidor Windows.");

        var fileName = File.Exists("/bin/sh") ? "/bin/sh" : FindOnPath("sh")
            ?? throw new InvalidOperationException("sh não encontrado neste sistema.");
        return (fileName, ["-c", command], ShellKind.Sh);
    }

    private static string? FindOnPath(string fileName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
            return null;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE").Split(';')
            : [""];

        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            foreach (var ext in extensions)
            {
                var candidateName = fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ext;
                var candidate = Path.Combine(dir, candidateName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
