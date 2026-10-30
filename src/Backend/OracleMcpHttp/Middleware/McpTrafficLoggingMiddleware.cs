using System.Text.RegularExpressions;

namespace OracleMcpHttp.Middleware;

public sealed partial class McpTrafficLoggingMiddleware(RequestDelegate next, ILogger<McpTrafficLoggingMiddleware> logger)
{
    // O x-api-key carrega a connection string (com senha) do Oracle: nunca vai para o log.
    private static readonly string[] SensitiveHeaders = [McpApiKey.HeaderName, "Authorization"];

    // Redige o parâmetro "password" da tool execute_shell_command.
    [GeneratedRegex("\"password\"\\s*:\\s*\"[^\"]*\"", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordFieldRegex();

    // Redige a senha de connection strings passadas no parâmetro connection_string da tool connect
    // (Password=...; ou Pwd=...;, inclusive com o valor entre aspas).
    [GeneratedRegex(@"((?:password|pwd)\s*=\s*)(\\""[^\\]*\\""|'[^']*'|[^;""'\\]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionSecretRegex();

    private static string RedactSecrets(string body) =>
        ConnectionSecretRegex().Replace(PasswordFieldRegex().Replace(body, "\"password\":\"***\""), "$1***");

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = string.Join(", ", context.Request.Headers
            .Where(h => !string.Equals(h.Key, "Cookie", StringComparison.OrdinalIgnoreCase))
            .Select(h => $"{h.Key}={(SensitiveHeaders.Contains(h.Key, StringComparer.OrdinalIgnoreCase) ? "***" : h.Value.ToString())}"));

        // O GET /mcp abre o canal SSE do transporte MCP e fica aberto por toda a sessão — bufferizar
        // a resposta faria o canal nunca ser entregue de forma incremental e acumularia em memória.
        if (HttpMethods.IsGet(context.Request.Method))
        {
            logger.LogInformation(
                "MCP request: {Method} {Path} | Headers: {Headers} | Body: (canal SSE, não bufferizado)",
                context.Request.Method, context.Request.Path, headers);
            await next(context);
            return;
        }

        context.Request.EnableBuffering();
        using var requestReader = new StreamReader(context.Request.Body, leaveOpen: true);
        var requestBody = RedactSecrets(await requestReader.ReadToEndAsync());
        context.Request.Body.Position = 0;

        logger.LogInformation(
            "MCP request: {Method} {Path} | Headers: {Headers} | Body: {Body}",
            context.Request.Method, context.Request.Path, headers, requestBody);

        var originalResponseBody = context.Response.Body;
        await using var responseBuffer = new MemoryStream();
        context.Response.Body = responseBuffer;

        try
        {
            await next(context);
        }
        finally
        {
            responseBuffer.Position = 0;
            var responseBody = RedactSecrets(await new StreamReader(responseBuffer).ReadToEndAsync());
            responseBuffer.Position = 0;
            await responseBuffer.CopyToAsync(originalResponseBody);
            context.Response.Body = originalResponseBody;

            logger.LogInformation(
                "MCP response: {StatusCode} {Path} | Body: {Body}",
                context.Response.StatusCode, context.Request.Path, responseBody);
        }
    }
}
