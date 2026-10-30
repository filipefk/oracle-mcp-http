namespace McpToolkit.Shell;

public sealed record ShellAvailabilityInfo(string Shell, bool Available);

public sealed record ShellInfoResult(
    string OperatingSystem,
    string DefaultShell,
    IReadOnlyList<ShellAvailabilityInfo> AvailableShells,
    string WorkingDirectory,
    int TimeoutSeconds,
    int MaxOutputChars,
    bool RequiresPassword);
