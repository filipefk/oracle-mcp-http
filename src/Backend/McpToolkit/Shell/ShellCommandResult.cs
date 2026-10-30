namespace McpToolkit.Shell;

public sealed record ShellCommandResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut,
    bool Truncated,
    long DurationMs,
    string Shell,
    string WorkingDirectory);
