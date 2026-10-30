using GeraApiKey;
using McpToolkit;
using Oracle.ManagedDataAccess.Client;

namespace OracleMcpHttp.Endpoints;

// Endpoints para gerar a API Key do header x-api-key, desfazê-la e testar a connection string.
// Protegidos pelo McpApiKeyMiddleware em modo Raw: aqui o x-api-key é a chave MCP em texto puro,
// conferida contra McpAuth:ApiKeys. São POST para os segredos irem no corpo, nunca na URL.
public static class ApiKeyEndpoints
{
    public sealed record GenerateApiKeyRequest(string? McpApiKey, string? ConnectionString);

    public sealed record GenerateApiKeyResponse(string ApiKey);

    public sealed record DecodeApiKeyRequest(string? ApiKey);

    public sealed record DecodeApiKeyResponse(string McpApiKey, string? ConnectionString);

    public sealed record TestConnectionRequest(string? ConnectionString);

    private static readonly TimeSpan TestConnectionTimeout = TimeSpan.FromSeconds(30);

    public static IEndpointRouteBuilder MapApiKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api-key");

        group.MapPost("/generate", (GenerateApiKeyRequest request) =>
        {
            // Sem a chave MCP a key gerada nunca passaria no McpApiKeyMiddleware — falha aqui, com motivo.
            if (string.IsNullOrWhiteSpace(request.McpApiKey))
                return Results.BadRequest(new { error = "Informe o mcpApiKey." });

            var apiKey = new McpApiKey(request.McpApiKey, request.ConnectionString?.Trim());
            try
            {
                return Results.Ok(new GenerateApiKeyResponse(ApiKeyGenerator.Generate(apiKey.ToValues())));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/decode", (DecodeApiKeyRequest request) =>
        {
            if (!ApiKeyGenerator.TryParse(request.ApiKey, out var values))
                return Results.BadRequest(new { error = "API Key inválida." });

            var apiKey = McpApiKey.FromValues(values);
            return Results.Ok(new DecodeApiKeyResponse(apiKey.McpKey, apiKey.OracleConnectionString));
        });

        // Abre e fecha uma conexão com a connection string informada, para a tela validar as credenciais
        // antes de gerar a key. A connection string não é guardada nem logada.
        group.MapPost("/test-connection", async (TestConnectionRequest request, ILogger<TestConnectionRequest> logger, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.ConnectionString))
                return Results.BadRequest(new { error = "Informe a connection string." });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TestConnectionTimeout);
            try
            {
                var info = await OracleConnectionManager.TestConnectionAsync(request.ConnectionString.Trim(), timeout.Token);
                return Results.Ok(info);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.BadRequest(new { error = $"Tempo esgotado ({TestConnectionTimeout.TotalSeconds:0}s) ao conectar no Oracle." });
            }
            catch (OracleException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = $"Connection string inválida: {ex.Message}" });
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                logger.LogWarning("Falha no teste de conexão Oracle: {Message}", ex.Message);
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return app;
    }
}
