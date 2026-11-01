using McpToolkit.Ddl;
using Oracle.ManagedDataAccess.Client;
using System.Text;
using System.Text.Json.Nodes;

namespace McpToolkit.Catalog;

// Portado de OracleTools2026.Core\Data\DatabaseObjectRepository (SearchAsync, GetRecentlyCompiledAsync,
// GetInvalidAsync e GetCompileErrorsAsync), usados pela tela "Extract DDL Oracle".
internal sealed class OracleObjectRepository(OracleConnection connection, int commandTimeoutSeconds)
{
    // TABLE não existe na tela original; foi incluída porque o extract_ddl do MCP gera DDL de tabelas.
    public static readonly IReadOnlyList<string> SupportedTypes =
        ["FUNCTION", "JOB", "PACKAGE", "PROCEDURE", "SEQUENCE", "TABLE", "TRIGGER", "TYPE", "VIEW", "JAVA SOURCE"];

    private const string NormalizedType = """
        CASE OBJECT_TYPE WHEN 'PACKAGE BODY' THEN 'PACKAGE' WHEN 'TYPE BODY' THEN 'TYPE' ELSE OBJECT_TYPE END
        """;

    // A tela original também exclui 'TABLE'; aqui tabelas entram para listar as alteradas recentemente.
    private const string RecentlyCompiledExcludedTypes =
        "'TABLE PARTITION', 'TABLE SUBPARTITION', 'SYNONYM', 'INDEX', 'INDEX PARTITION', " +
        "'LOB PARTITION', 'DIRECTORY', 'QUEUE', 'TYPE', 'TYPE BODY', 'LOB'";

    public async Task<List<string>> GetOwnersAsync(CancellationToken cancellationToken)
    {
        await using var command = CreateCommand("SELECT DISTINCT OWNER FROM ALL_OBJECTS ORDER BY OWNER");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var owners = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            owners.Add(reader.GetString(0));
        }

        return owners;
    }

    public Task<List<DatabaseObject>> SearchAsync(string? owner, string? type, string? name, CancellationToken cancellationToken)
    {
        var where = new StringBuilder($"OBJECT_TYPE IN ({ToSqlList(SupportedTypes.Concat(["PACKAGE BODY", "TYPE BODY"]))})");
        var parameters = new List<(string, object?)>();
        AppendFilters(where, parameters, owner, type, name);

        return QueryObjectsAsync(where.ToString(), having: null, orderBy: "OWNER, OBJECT_TYPE, OBJECT_NAME", parameters, cancellationToken);
    }

    public Task<List<DatabaseObject>> GetRecentlyCompiledAsync(int days, CancellationToken cancellationToken)
    {
        var where = $"OBJECT_TYPE NOT IN ({RecentlyCompiledExcludedTypes})";
        const string having = "CASE WHEN MIN(OBJECT_TYPE) = 'JOB' THEN MIN(CREATED) ELSE MAX(LAST_DDL_TIME) END > SYSDATE - :days";

        return QueryObjectsAsync(where, having, "LAST_DDL_TIME DESC, OWNER, OBJECT_NAME", [("days", days)], cancellationToken);
    }

    public Task<List<DatabaseObject>> GetInvalidAsync(string? owner, string? type, string? name, CancellationToken cancellationToken)
    {
        var where = new StringBuilder("1 = 1");
        var parameters = new List<(string, object?)>();
        AppendFilters(where, parameters, owner, type, name);
        const string having = "MAX(CASE WHEN STATUS <> 'VALID' THEN 1 ELSE 0 END) = 1";

        return QueryObjectsAsync(where.ToString(), having, "LAST_DDL_TIME DESC, OWNER, OBJECT_NAME", parameters, cancellationToken);
    }

    private static void AppendFilters(StringBuilder where, List<(string, object?)> parameters, string? owner, string? type, string? name)
    {
        if (!string.IsNullOrWhiteSpace(owner))
        {
            where.Append(" AND OWNER = :owner");
            parameters.Add(("owner", owner.Trim().ToUpperInvariant()));
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            where.Append($" AND {NormalizedType} = :objectType");
            parameters.Add(("objectType", type.Trim().ToUpperInvariant()));
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            where.Append(" AND UPPER(OBJECT_NAME) LIKE '%' || :objectName || '%'");
            parameters.Add(("objectName", name.Trim().ToUpperInvariant()));
        }
    }

    private async Task<List<DatabaseObject>> QueryObjectsAsync(
        string where,
        string? having,
        string orderBy,
        IReadOnlyList<(string Name, object? Value)> parameters,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT OWNER, OBJECT_NAME, OBJECT_TYPE, CREATED, LAST_DDL_TIME, STATUS
            FROM (
                SELECT OWNER,
                       OBJECT_NAME,
                       {NormalizedType} AS OBJECT_TYPE,
                       MIN(CREATED) AS CREATED,
                       MAX(LAST_DDL_TIME) AS LAST_DDL_TIME,
                       CASE WHEN MAX(CASE WHEN STATUS <> 'VALID' THEN 1 ELSE 0 END) = 1 THEN 'INVALID' ELSE 'VALID' END AS STATUS
                FROM ALL_OBJECTS
                WHERE {where}
                GROUP BY OWNER, OBJECT_NAME, {NormalizedType}
                {(having is null ? "" : "HAVING " + having)}
            )
            ORDER BY {orderBy}
            """;

        var objects = new List<DatabaseObject>();
        await using (var command = CreateCommand(sql, [.. parameters]))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                objects.Add(new DatabaseObject(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetDateTimeOrNull(3),
                    reader.GetDateTimeOrNull(4),
                    reader.GetStringOrEmpty(5)));
            }
        }

        return await FillCompileErrorsAsync(objects, cancellationToken);
    }

    private async Task<List<DatabaseObject>> FillCompileErrorsAsync(List<DatabaseObject> objects, CancellationToken cancellationToken)
    {
        var invalid = objects.Where(o => o.Status == "INVALID").ToList();
        if (invalid.Count == 0)
        {
            return objects;
        }

        // Com um único objeto inválido filtra no banco; com vários, uma só consulta em ALL_ERRORS é mais barata que N consultas.
        (string Owner, string Name)? filter = invalid.Count == 1 ? (invalid[0].Owner, invalid[0].Name) : null;
        var errors = await GetCompileErrorsAsync(filter, cancellationToken);
        if (errors.Count == 0)
        {
            return objects;
        }

        return objects
            .Select(o => o.Status == "INVALID" && errors.TryGetValue((o.Owner, o.Name, o.Type), out var message) ? o with { ErrorMessage = message } : o)
            .ToList();
    }

    private async Task<Dictionary<(string Owner, string Name, string Type), string>> GetCompileErrorsAsync(
        (string Owner, string Name)? filter, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT ERR.OWNER, ERR.NAME, ERR.TYPE, ERR.LINE, ERR.POSITION, ERR.TEXT, SOU.TEXT AS SOURCE_LINE
            FROM ALL_ERRORS ERR
                LEFT JOIN ALL_SOURCE SOU
                ON ERR.OWNER = SOU.OWNER
                AND ERR.NAME = SOU.NAME
                AND ERR.TYPE = SOU.TYPE
                AND ERR.LINE = SOU.LINE
            WHERE ERR.ATTRIBUTE = 'ERROR'
              AND UPPER(ERR.TEXT) NOT LIKE '%STATEMENT IGNORED%'
              {(filter is null ? "" : "AND ERR.OWNER = :owner AND ERR.NAME = :objectName")}
            ORDER BY ERR.OWNER, ERR.NAME, ERR.TYPE, ERR.SEQUENCE
            """;

        await using var command = filter is { } f
            ? CreateCommand(sql, ("owner", f.Owner), ("objectName", f.Name))
            : CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var errors = new Dictionary<(string, string, string), StringBuilder>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var type = reader.GetString(2) switch
            {
                "PACKAGE BODY" => "PACKAGE",
                "TYPE BODY" => "TYPE",
                var other => other
            };
            var key = (reader.GetString(0), reader.GetString(1), type);
            if (!errors.TryGetValue(key, out var message))
            {
                message = new StringBuilder();
                errors[key] = message;
            }
            else
            {
                message.Append("\r\n");
            }

            var line = reader.GetNumberText(3);
            if (line is not ("" or "0"))
            {
                message.Append($"Nº LINHA:{line} - Nº COLUNA:{reader.GetNumberText(4)} - ");
            }

            message.Append("MENSAGEM: ").Append(reader.GetStringOrEmpty(5).ReplaceLineEndings(" "));

            var sourceLine = reader.GetStringOrEmpty(6).Trim();
            if (sourceLine.Length > 0)
            {
                message.Append($" - TEXTO DA LINHA {line}: {sourceLine}");
            }
        }

        return errors.ToDictionary(e => e.Key, e => e.Value.ToString());
    }

    private OracleCommand CreateCommand(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateTextCommand(sql, parameters);
        if (commandTimeoutSeconds > 0)
        {
            command.CommandTimeout = commandTimeoutSeconds;
        }

        return command;
    }

    private static string ToSqlList(IEnumerable<string> values) =>
        string.Join(", ", values.Select(v => $"'{v.Replace("'", "''")}'"));
}

internal sealed record DatabaseObject(string Owner, string Name, string Type, DateTime? CreatedAt, DateTime? LastDdlAt, string Status)
{
    public string ErrorMessage { get; init; } = "";

    public string QualifiedName => $"{Owner}.{Name}";

    public JsonObject ToJson() => new()
    {
        ["owner"] = Owner,
        ["name"] = Name,
        ["type"] = Type,
        ["qualifiedName"] = QualifiedName,
        ["createdAt"] = CatalogFormat.Date(CreatedAt),
        ["lastDdlAt"] = CatalogFormat.Date(LastDdlAt),
        ["status"] = Status,
        ["errorMessage"] = ErrorMessage.Length == 0 ? null : ErrorMessage
    };
}
