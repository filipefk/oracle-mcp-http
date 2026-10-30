using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace OracleMcpHttp.Middleware;

// Como o header x-api-key é lido: em /mcp ele é a key do GeraApiKey (o mcp_api_key é o
// primeiro valor); em /api-key é a própria chave MCP em texto puro — lá a key gerada é justamente
// o que os endpoints produzem/desfazem, então decodificar o header não faria sentido.
public enum McpKeyFormat
{
    Encoded,
    Raw
}

// Protege /mcp e /api-key: exige o header x-api-key e valida o mcp_api_key contra
// McpAuth:ApiKeys. Lista vazia rejeita tudo (fail-closed).
public sealed class McpApiKeyMiddleware(
    RequestDelegate next,
    IOptionsMonitor<McpAuthOptions> options,
    ILogger<McpApiKeyMiddleware> logger,
    McpKeyFormat keyFormat)
{
    private const string UnauthorizedMessage = "Não autorizado: informe um header x-api-key válido.";

    public async Task InvokeAsync(HttpContext context)
    {
        var mcpKey = ReadMcpKey(context);
        if (mcpKey is null)
        {
            await RejectAsync(context, $"header {McpApiKey.HeaderName} ausente ou inválido");
            return;
        }

        var authorizedKeys = options.CurrentValue.ApiKeys.Where(k => !string.IsNullOrEmpty(k)).ToList();
        if (authorizedKeys.Count == 0)
        {
            await RejectAsync(context, "nenhuma chave cadastrada em McpAuth:ApiKeys");
            return;
        }

        if (!IsAuthorized(mcpKey, authorizedKeys))
        {
            await RejectAsync(context, "mcp_api_key não autorizada");
            return;
        }

        await next(context);
    }

    private string? ReadMcpKey(HttpContext context)
    {
        if (keyFormat == McpKeyFormat.Raw)
        {
            var raw = McpApiKey.ReadHeader(context);
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }

        return McpApiKey.TryRead(context, out var apiKey) ? apiKey.McpKey : null;
    }

    // Compara com todas as chaves em tempo constante, sem sair na primeira coincidência.
    private static bool IsAuthorized(string mcpKey, IEnumerable<string> authorizedKeys)
    {
        var candidate = Encoding.UTF8.GetBytes(mcpKey);
        var authorized = false;
        foreach (var key in authorizedKeys)
            authorized |= CryptographicOperations.FixedTimeEquals(candidate, Encoding.UTF8.GetBytes(key));
        return authorized;
    }

    private Task RejectAsync(HttpContext context, string reason)
    {
        logger.LogWarning("Requisição rejeitada: {Method} {Path} | Motivo: {Reason}", context.Request.Method, context.Request.Path, reason);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new { error = UnauthorizedMessage });
    }
}
