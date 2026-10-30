using Oracle.ManagedDataAccess.Client;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace McpToolkit;

// Conversões entre o mundo JSON das tools e o ODP.NET, e ajustes do texto SQL.
public static partial class OracleSql
{
    // Blocos PL/SQL e objetos compilados precisam do ";" final; instruções SQL simples dão ORA-00911 com ele.
    [GeneratedRegex(@"^\s*(BEGIN|DECLARE|CREATE\s+(OR\s+REPLACE\s+)?((NON)?EDITIONABLE\s+)?(PROCEDURE|FUNCTION|PACKAGE|TRIGGER|TYPE|LIBRARY)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex PlSqlRegex();

    // "/" sozinho na última linha (terminador do SQL*Plus/SQL Developer).
    [GeneratedRegex(@"(^|\n)\s*/\s*$")]
    private static partial Regex SqlPlusTerminatorRegex();

    public static bool IsPlSql(string sql) => PlSqlRegex().IsMatch(sql);

    public static string NormalizeSql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new InvalidOperationException("O parâmetro 'sql' não pode ser vazio.");

        var text = SqlPlusTerminatorRegex().Replace(sql.Trim(), string.Empty).Trim();

        if (!IsPlSql(text))
            text = text.TrimEnd(';', ' ', '\t', '\r', '\n');

        if (text.Length == 0)
            throw new InvalidOperationException("O parâmetro 'sql' não pode ser vazio.");

        return text;
    }

    public static JsonObject? ParseParameters(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson)) return null;

        try
        {
            return JsonNode.Parse(parametersJson) as JsonObject
                ?? throw new InvalidOperationException("O parâmetro 'parameters_json' deve ser um objeto JSON.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException($"O parâmetro 'parameters_json' contém um JSON inválido: {ex.Message}");
        }
    }

    public static void AddParameters(OracleCommand cmd, JsonObject? parameters)
    {
        // Sem BindByName o ODP.NET associa os parâmetros pela posição, ignorando os nomes.
        cmd.BindByName = true;
        if (parameters is null) return;

        foreach (var (key, value) in parameters)
            cmd.Parameters.Add(new OracleParameter(key.TrimStart(':'), JsonValueToClr(value)));
    }

    // Booleanos viram 1/0: SQL do Oracle anterior ao 23ai não tem tipo BOOLEAN.
    public static object JsonValueToClr(JsonNode? node) => node switch
    {
        null => DBNull.Value,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? 1 : 0,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<decimal>(out var m) => m,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString()
    };

    public static JsonNode? ReadValue(OracleDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;

        // NUMBER do Oracle tem até 38 dígitos e não cabe sempre num decimal do .NET (28-29 dígitos):
        // quando não cabe, devolve o valor exato como texto em vez de estourar.
        if (reader.GetFieldType(ordinal) == typeof(decimal))
        {
            var oracleDecimal = reader.GetOracleDecimal(ordinal);
            try
            {
                return JsonValue.Create(oracleDecimal.Value);
            }
            catch (Exception ex) when (ex is OverflowException or InvalidCastException)
            {
                return JsonValue.Create(oracleDecimal.ToString());
            }
        }

        object? value;
        try
        {
            value = reader.GetValue(ordinal);
        }
        catch (Exception ex) when (ex is OverflowException or InvalidCastException or NotSupportedException)
        {
            value = reader.GetProviderSpecificValue(ordinal)?.ToString();
        }

        return ConvertValue(value);
    }

    public static JsonNode? ConvertValue(object? value) => value switch
    {
        null or DBNull => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        byte or sbyte or short or ushort or int or uint or long => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        ulong ul => JsonValue.Create(ul),
        decimal m => JsonValue.Create(m),
        float or double => ConvertFloatingPoint(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        DateTime dt => JsonValue.Create(dt.ToString("O", CultureInfo.InvariantCulture)),
        DateTimeOffset dto => JsonValue.Create(dto.ToString("O", CultureInfo.InvariantCulture)),
        TimeSpan ts => JsonValue.Create(ts.ToString("c", CultureInfo.InvariantCulture)),
        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
        Guid g => JsonValue.Create(g.ToString()),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture))
    };

    // NaN/Infinity (BINARY_FLOAT/BINARY_DOUBLE) não existem em JSON.
    private static JsonNode ConvertFloatingPoint(double d) =>
        double.IsFinite(d)
            ? JsonValue.Create(d)
            : JsonValue.Create(d.ToString(CultureInfo.InvariantCulture))!;

    // Colunas com o mesmo nome (ex: SELECT a.id, b.id) viram ID, ID_2, ID_3... — senão o JsonObject da linha rejeitaria a chave repetida.
    public static string[] UniqueColumnNames(IReadOnlyList<string> names)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new string[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            var name = string.IsNullOrEmpty(names[i]) ? $"COLUMN_{i + 1}" : names[i];
            var candidate = name;
            for (var suffix = 2; !used.Add(candidate); suffix++)
                candidate = $"{name}_{suffix}";
            result[i] = candidate;
        }
        return result;
    }
}
