using GeraApiKey;
using System.Diagnostics.CodeAnalysis;

namespace OracleMcpHttp;

// Conteúdo do header x-api-key, gerado pelo GeraApiKey.ApiKeyGenerator a partir de
// [mcpApiKey, oracleConnectionString] — nesta ordem. FromValues/ToValues são o único lugar
// do código que conhece essa ordem; a biblioteca GeraApiKey só vê uma lista de strings.
// A connection string é opcional: vazia ou ausente cai no fallback do servidor (Oracle:ConnectionString).
public sealed class McpApiKey(string mcpKey, string? oracleConnectionString)
{
    public const string HeaderName = "x-api-key";

    public string McpKey { get; } = mcpKey;
    public string? OracleConnectionString { get; } = oracleConnectionString;

    public static McpApiKey FromValues(IReadOnlyList<string> values) =>
        new(values.Count > 0 ? values[0] : string.Empty, ValueAt(values, 1));

    // Valores vazios do fim são descartados: uma key só com a chave MCP não carrega separador inútil.
    public IReadOnlyList<string> ToValues()
    {
        var values = new List<string> { McpKey, OracleConnectionString ?? string.Empty };
        while (values.Count > 1 && values[^1].Length == 0)
            values.RemoveAt(values.Count - 1);
        return values;
    }

    public static bool TryRead(HttpContext? context, [NotNullWhen(true)] out McpApiKey? apiKey)
    {
        apiKey = null;
        var header = ReadHeader(context);
        if (!ApiKeyGenerator.TryParse(header, out var values)) return false;

        apiKey = FromValues(values);
        return true;
    }

    public static string? ReadHeader(HttpContext? context) =>
        context?.Request.Headers[HeaderName].FirstOrDefault();

    private static string? ValueAt(IReadOnlyList<string> values, int index) =>
        index < values.Count && !string.IsNullOrWhiteSpace(values[index]) ? values[index] : null;
}
