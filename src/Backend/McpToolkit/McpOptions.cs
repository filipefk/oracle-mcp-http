namespace McpToolkit;

public sealed class McpOptions
{
    // Connection string do servidor (Oracle:ConnectionString ou variável ORACLE_CONNECTION).
    // Usada quando a API Key do cliente não traz uma connection string própria.
    public string ConnectionString { get; set; } = string.Empty;

    // Tempo de inatividade até a conexão de uma sessão MCP ser fechada; <= 0 desliga o fechamento automático.
    public int IdleTimeoutMinutes { get; set; } = 10;

    // Timeout de cada comando SQL; 0 = sem limite.
    public int CommandTimeoutSeconds { get; set; }
}
