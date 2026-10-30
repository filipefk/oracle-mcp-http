namespace McpToolkit;

// Abstrai o acesso à requisição HTTP em curso, para que o McpToolkit não dependa
// diretamente de Microsoft.AspNetCore.Http nem do formato do header x-api-key
// (implementado no host, OracleMcpHttp).
public interface IRequestContext
{
    // Connection string do Oracle gravada na API Key do cliente, ou null se a key não traz uma.
    string? OracleConnectionString { get; }

    // Id da sessão MCP (header Mcp-Session-Id), ou null fora de uma sessão.
    // Cada sessão tem a sua própria conexão Oracle.
    string? SessionId { get; }
}
