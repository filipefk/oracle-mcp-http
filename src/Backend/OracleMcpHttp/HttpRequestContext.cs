using McpToolkit;

namespace OracleMcpHttp;

// Decodifica o header x-api-key da requisição em curso (já validado pelo McpApiKeyMiddleware)
// e expõe o id da sessão MCP, usado para separar as conexões Oracle de cada cliente.
public sealed class HttpRequestContext(IHttpContextAccessor httpContextAccessor) : IRequestContext
{
    private const string SessionIdHeader = "Mcp-Session-Id";

    public string? OracleConnectionString =>
        McpApiKey.TryRead(httpContextAccessor.HttpContext, out var apiKey) ? apiKey.OracleConnectionString : null;

    public string? SessionId =>
        httpContextAccessor.HttpContext?.Request.Headers[SessionIdHeader].FirstOrDefault();
}
