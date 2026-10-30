namespace McpToolkit.Shell;

public sealed class ShellOptions
{
    public bool Enabled { get; set; } = false;

    public ShellKind DefaultShell { get; set; } = ShellKind.Auto;

    public string WorkingDirectory { get; set; } = "";

    public int TimeoutSeconds { get; set; } = 60;

    public int MaxOutputChars { get; set; } = 30000;

    public string Password { get; set; } = "";

    public string[] AllowedCommands { get; set; } = [];

    public string[] BlockedCommands { get; set; } = [];
}
