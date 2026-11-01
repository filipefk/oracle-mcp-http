using Oracle.ManagedDataAccess.Client;
using System.Text;
using System.Text.RegularExpressions;

namespace McpToolkit.Ddl;

// Portado de OracleTools2026.Core\Data\DdlExtractor.cs, com suporte a tabela (CREATE TABLE, comentários,
// constraints, índices e triggers montados a partir das views ALL_*).
internal sealed partial class OracleDdlExtractor(OracleConnection connection, int commandTimeoutSeconds)
{
    private sealed record ObjectInfo(string Type, DateTime? CreatedAt, DateTime? LastDdlAt, string Status);

    private sealed record TableColumn(
        string Name, string Type, string Nullable, string Virtual, string Identity, string DefaultOnNull, string Default);

    private sealed record TableConstraint(
        string Name, string Type, string ROwner, string RName, string DeleteRule, string Status,
        string Deferrable, string Deferred, string Validated, bool GeneratedName, string Condition);

    public async Task<DdlExtractionResult> ExtractAsync(string qualifiedName, DdlExtractionOptions options, CancellationToken cancellationToken)
    {
        var parts = qualifiedName.Replace("\"", "").Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2)
        {
            return DdlExtractionResult.Failure(qualifiedName, $"Nome de objeto inválido: {qualifiedName}");
        }

        try
        {
            var owner = parts.Length == 2 ? parts[0].ToUpperInvariant() : await GetCurrentSchemaAsync(cancellationToken);
            var name = parts[^1].ToUpperInvariant();
            var normalizedName = $"{owner}.{name}";

            try
            {
                var info = await GetObjectInfoAsync(owner, name, cancellationToken);
                if (info is null)
                {
                    return DdlExtractionResult.Failure(normalizedName, $"Objeto {normalizedName} não encontrado");
                }

                var source = info.Type switch
                {
                    "FUNCTION" or "PROCEDURE" or "TRIGGER" or "PACKAGE" or "PACKAGE BODY" or "TYPE" or "TYPE BODY" or "JAVA SOURCE" =>
                        await ExtractStoredSourceAsync(owner, name, info.Type, options.InlineCompileErrors, cancellationToken),
                    "VIEW" => await ExtractViewAsync(owner, name, cancellationToken),
                    "SEQUENCE" => await ExtractSequenceAsync(owner, name, cancellationToken),
                    "JOB" => await ExtractJobAsync(owner, name, cancellationToken),
                    "TABLE" => await ExtractTableAsync(owner, name, options.InlineCompileErrors, cancellationToken),
                    _ => null
                };

                if (source is null)
                {
                    return DdlExtractionResult.Failure(normalizedName, $"Tipo de objeto {info.Type} não suportado", info.Type);
                }

                if (source.Length == 0)
                {
                    return DdlExtractionResult.Failure(normalizedName, $"DDL não encontrado para o objeto {normalizedName}", info.Type);
                }

                var ddl = new StringBuilder(source);
                if (info.Type is not "JOB")
                {
                    ddl.Append(await ExtractSynonymsAsync(owner, name, cancellationToken));
                }

                if (info.Type is not ("JOB" or "JAVA SOURCE"))
                {
                    ddl.Append(await ExtractGrantsAsync(owner, name, cancellationToken));
                }

                var body = ddl.ToString().TrimEnd('\r', '\n');
                if (options.IncludeHeader)
                {
                    body = await BuildHeaderAsync(owner, name, info, cancellationToken) + "\n" + body;
                }

                var objectType = info.Type switch
                {
                    "PACKAGE BODY" => "PACKAGE",
                    "TYPE BODY" => "TYPE",
                    var other => other
                };

                return new DdlExtractionResult(normalizedName, objectType, body.ReplaceLineEndings("\r\n"), null);
            }
            catch (OracleException ex)
            {
                return DdlExtractionResult.Failure(normalizedName, ex.Message);
            }
        }
        catch (OracleException ex)
        {
            return DdlExtractionResult.Failure(qualifiedName, ex.Message);
        }
    }

    private OracleCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateTextCommand(sql, parameters);
        if (commandTimeoutSeconds > 0)
        {
            command.CommandTimeout = commandTimeoutSeconds;
        }

        return command;
    }

    private async Task<string> GetCurrentSchemaAsync(CancellationToken cancellationToken)
    {
        await using var command = Command("SELECT SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') FROM DUAL");
        return (await command.ExecuteScalarAsync(cancellationToken) as string ?? "").ToUpperInvariant();
    }

    private async Task<ObjectInfo?> GetObjectInfoAsync(string owner, string name, CancellationToken cancellationToken)
    {
        // Índices e LOBs ficam em outro namespace e podem ter o mesmo nome da tabela.
        const string sql = """
            SELECT MIN(OBJECT_TYPE), MIN(CREATED), MAX(LAST_DDL_TIME), MIN(STATUS)
            FROM ALL_OBJECTS
            WHERE OWNER = :owner
              AND OBJECT_NAME = :objectName
              AND OBJECT_TYPE NOT IN ('SYNONYM', 'TABLE PARTITION', 'TABLE SUBPARTITION')
              AND OBJECT_TYPE NOT LIKE 'INDEX%'
              AND OBJECT_TYPE NOT LIKE 'LOB%'
            """;

        await using var command = Command(sql, ("owner", owner), ("objectName", name));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            return null;
        }

        return new ObjectInfo(
            reader.GetString(0).Trim().ToUpperInvariant(),
            reader.GetDateTimeOrNull(1),
            reader.GetDateTimeOrNull(2),
            reader.GetStringOrEmpty(3));
    }

    private async Task<string> BuildHeaderAsync(string owner, string name, ObjectInfo info, CancellationToken cancellationToken)
    {
        var serverInfo = await GetServerInfoAsync(connection.DataSource, cancellationToken);

        var header = new StringBuilder()
            .Append("-- Fonte extraído pelo Oracle Tools\n")
            .Append($"-- Dados de conexão: {serverInfo}\n")
            .Append($"-- Objeto criado em: {info.CreatedAt:dd/MM/yy HH:mm:ss}\n")
            .Append($"-- Última compilação deste objeto: {info.LastDdlAt:dd/MM/yy HH:mm:ss}\n")
            .Append($"-- Status deste objeto: {info.Status}\n");

        var errors = await GetCompileErrorsAsync(owner, name, cancellationToken);
        var messageLines = errors.Values.SelectMany(m => m.Split("\r\n")).Where(l => l.Length > 0).ToList();
        if (messageLines.Count > 0)
        {
            header.Append("-- Mensagem:\n");
            foreach (var line in messageLines)
            {
                header.Append($"--   {line}\n");
            }
        }

        return header.ToString();
    }

    // Portado de DatabaseObjectRepository.GetServerInfoAsync.
    private async Task<string> GetServerInfoAsync(string dataSource, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TO_CHAR(SYSDATE, 'DD/MM/YY HH24:MI:SS'),
                   UPPER(SYS_CONTEXT('USERENV', 'SERVER_HOST')),
                   UPPER(SYS_CONTEXT('USERENV', 'INSTANCE_NAME')),
                   UPPER(SYS_CONTEXT('USERENV', 'CURRENT_USER'))
            FROM DUAL
            """;

        await using var command = Command(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return "";
        }

        return $"{reader.GetString(0)} - Host: {reader.GetStringOrEmpty(1)} - Instance: {reader.GetStringOrEmpty(2)} " +
               $"- User: {reader.GetStringOrEmpty(3)} - Alias: {dataSource}";
    }

    // Portado de DatabaseObjectRepository.GetCompileErrorsAsync (variante filtrada por owner/nome).
    private async Task<Dictionary<(string Owner, string Name, string Type), string>> GetCompileErrorsAsync(
        string owner, string name, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ERR.OWNER, ERR.NAME, ERR.TYPE, ERR.LINE, ERR.POSITION, ERR.TEXT, SOU.TEXT AS SOURCE_LINE
            FROM ALL_ERRORS ERR
                LEFT JOIN ALL_SOURCE SOU
                ON ERR.OWNER = SOU.OWNER
                AND ERR.NAME = SOU.NAME
                AND ERR.TYPE = SOU.TYPE
                AND ERR.LINE = SOU.LINE
            WHERE ERR.ATTRIBUTE = 'ERROR'
              AND UPPER(ERR.TEXT) NOT LIKE '%STATEMENT IGNORED%'
              AND ERR.OWNER = :owner AND ERR.NAME = :objectName
            ORDER BY ERR.OWNER, ERR.NAME, ERR.TYPE, ERR.SEQUENCE
            """;

        await using var command = Command(sql, ("owner", owner), ("objectName", name));
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

    private async Task<string> ExtractStoredSourceAsync(
        string owner, string name, string objectType, bool inlineErrors, CancellationToken cancellationToken)
    {
        var (specType, bodyType) = objectType switch
        {
            "PACKAGE" or "PACKAGE BODY" => ("PACKAGE", "PACKAGE BODY"),
            "TYPE" or "TYPE BODY" => ("TYPE", "TYPE BODY"),
            var other => (other, (string?)null)
        };

        var ddl = new StringBuilder();
        var spec = await ReadSourceAsync(owner, name, specType, inlineErrors, cancellationToken);
        if (spec.Length == 0)
        {
            return "";
        }

        if (specType == "TRIGGER" && await IsTriggerDisabledAsync(owner, name, cancellationToken))
        {
            spec = InsertDisableClause(spec);
        }

        ddl.Append(spec);

        if (bodyType is not null)
        {
            ddl.Append(await ReadSourceAsync(owner, name, bodyType, inlineErrors, cancellationToken));
        }

        return ddl.ToString();
    }

    private async Task<string> ReadSourceAsync(
        string owner, string name, string sourceType, bool inlineErrors, CancellationToken cancellationToken)
    {
        var errorsByLine = inlineErrors
            ? await ReadErrorsByLineAsync(owner, name, sourceType, cancellationToken)
            : new Dictionary<int, string>();

        const string sql = """
            SELECT TEXT, LINE
            FROM ALL_SOURCE
            WHERE OWNER = :owner
              AND NAME = :objectName
              AND TYPE = :sourceType
            ORDER BY LINE
            """;

        await using var command = Command(sql, ("owner", owner), ("objectName", name), ("sourceType", sourceType));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var source = new StringBuilder();
        var isFirstLine = true;
        while (await reader.ReadAsync(cancellationToken))
        {
            var line = reader.GetStringOrEmpty(0);
            var lineNumber = reader.GetInt32(1);

            if (isFirstLine)
            {
                source.Append("CREATE OR REPLACE ");
                if (sourceType == "JAVA SOURCE")
                {
                    source.Append($"AND COMPILE JAVA SOURCE NAMED {owner}.{name} AS\n");
                }
                else
                {
                    line = NormalizeFirstLine(line, owner, name) + "\n";
                }

                isFirstLine = false;
            }

            if (errorsByLine.TryGetValue(lineNumber, out var error))
            {
                line = line.TrimEnd('\r', '\n') + " -- ###ERRO### " + error + "\n";
            }

            source.Append(line);
        }

        if (source.Length == 0)
        {
            return "";
        }

        if (source[^1] is not ('\n' or '\r'))
        {
            source.Append('\n');
        }

        return source.Append("/\n\n").ToString();
    }

    private static string NormalizeFirstLine(string line, string owner, string name)
    {
        line = line.Trim().ToUpperInvariant().Replace("\"", "");
        if (!line.Contains(owner + ".", StringComparison.Ordinal))
        {
            line = Regex.Replace(line, $@"\b{Regex.Escape(name)}\b", $"{owner}.{name}");
        }

        return MultipleSpacesRegex().Replace(line, " ");
    }

    private async Task<Dictionary<int, string>> ReadErrorsByLineAsync(
        string owner, string name, string sourceType, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT LINE, TEXT
            FROM ALL_ERRORS
            WHERE OWNER = :owner
              AND NAME = :objectName
              AND TYPE = :sourceType
            ORDER BY LINE, SEQUENCE
            """;

        await using var command = Command(sql, ("owner", owner), ("objectName", name), ("sourceType", sourceType));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var errors = new Dictionary<int, string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var message = MultipleSpacesRegex().Replace(reader.GetStringOrEmpty(1).Replace("\r", ""), " ").Replace("\n", "/").Trim();
            if (message.Length == 0)
            {
                continue;
            }

            var line = reader.GetInt32(0);
            errors[line] = errors.TryGetValue(line, out var existing) ? $"{existing} / {message}" : message;
        }

        return errors;
    }

    private async Task<bool> IsTriggerDisabledAsync(string owner, string name, CancellationToken cancellationToken)
    {
        const string sql = "SELECT STATUS FROM ALL_TRIGGERS WHERE OWNER = :owner AND TRIGGER_NAME = :objectName";
        await using var command = Command(sql, ("owner", owner), ("objectName", name));
        var status = await command.ExecuteScalarAsync(cancellationToken) as string;
        return string.Equals(status?.Trim(), "DISABLED", StringComparison.OrdinalIgnoreCase);
    }

    private static string InsertDisableClause(string triggerSource)
    {
        var lines = triggerSource.Split('\n');
        var index = Array.FindIndex(lines, l => BeginOrDeclareRegex().IsMatch(l));
        if (index < 0)
        {
            return triggerSource;
        }

        return string.Join('\n', lines.Take(index).Append("DISABLE").Concat(lines.Skip(index)));
    }

    private async Task<string> ExtractViewAsync(string owner, string name, CancellationToken cancellationToken)
    {
        const string columnsSql = """
            SELECT COLUMN_NAME
            FROM ALL_TAB_COLUMNS
            WHERE OWNER = :owner
              AND TABLE_NAME = :objectName
            ORDER BY COLUMN_ID
            """;

        var columns = new List<string>();
        await using (var command = Command(columnsSql, ("owner", owner), ("objectName", name)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(0));
            }
        }

        const string viewSql = "SELECT TEXT FROM ALL_VIEWS WHERE OWNER = :owner AND VIEW_NAME = :objectName";
        await using var viewCommand = Command(viewSql, ("owner", owner), ("objectName", name));
        viewCommand.InitialLONGFetchSize = -1;
        await using var viewReader = await viewCommand.ExecuteReaderAsync(cancellationToken);
        if (!await viewReader.ReadAsync(cancellationToken))
        {
            return "";
        }

        var text = viewReader.GetStringOrEmpty(0).TrimEnd();
        return new StringBuilder()
            .Append($"CREATE OR REPLACE VIEW {owner}.{name}\n(\n")
            .Append(string.Join(",\n", columns.Select(c => "   " + c)))
            .Append("\n)\nAS\n")
            .Append(text)
            .Append("\n/\n\n")
            .ToString();
    }

    private async Task<string> ExtractSequenceAsync(string owner, string name, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT LAST_NUMBER, INCREMENT_BY, MAX_VALUE, MIN_VALUE, CYCLE_FLAG, CACHE_SIZE, ORDER_FLAG
            FROM ALL_SEQUENCES
            WHERE SEQUENCE_OWNER = :owner
              AND SEQUENCE_NAME = :objectName
            """;

        await using var command = Command(sql, ("owner", owner), ("objectName", name));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return "";
        }

        var cacheSize = reader.GetNumberText(5);
        return new StringBuilder()
            .Append($"CREATE SEQUENCE {owner}.{name}\n")
            .Append($"  START WITH {reader.GetNumberText(0)}\n")
            .Append($"  INCREMENT BY {reader.GetNumberText(1)}\n")
            .Append($"  MAXVALUE {reader.GetNumberText(2)}\n")
            .Append($"  MINVALUE {reader.GetNumberText(3)}\n")
            .Append(reader.GetStringOrEmpty(4).Trim() == "N" ? "  NOCYCLE\n" : "  CYCLE\n")
            .Append(cacheSize is "0" or "" ? "  NOCACHE\n" : $"  CACHE {cacheSize}\n")
            .Append(reader.GetStringOrEmpty(6).Trim() == "N" ? "  NOORDER\n" : "  ORDER\n")
            .Append("/\n\n")
            .ToString();
    }

    private async Task<string> ExtractJobAsync(string owner, string name, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT OWNER, JOB_NAME, PROGRAM_OWNER, PROGRAM_NAME, JOB_TYPE, JOB_ACTION,
                   SCHEDULE_OWNER, SCHEDULE_NAME, REPEAT_INTERVAL, ENABLED, COMMENTS
            FROM ALL_SCHEDULER_JOBS
            WHERE OWNER = :owner
              AND JOB_NAME = :objectName
            """;

        await using var command = Command(sql, ("owner", owner), ("objectName", name));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return "";
        }

        var job = new StringBuilder()
            .Append("BEGIN\n")
            .Append("   DBMS_SCHEDULER.CREATE_JOB (\n")
            .Append($"   job_name           =>  '{reader.GetString(0)}.{reader.GetString(1)}',\n");

        if (reader.IsDBNull(4))
        {
            job.Append($"   program_name       =>  '{reader.GetStringOrEmpty(2)}.{reader.GetStringOrEmpty(3)}',\n");
        }
        else
        {
            job.Append($"   job_type           =>  '{reader.GetString(4)}',\n");
            job.Append($"   job_action         =>  '{Escape(reader.GetStringOrEmpty(5))}',\n");
        }

        if (reader.IsDBNull(8))
        {
            if (!reader.IsDBNull(7))
            {
                job.Append($"   schedule_name      =>  '{reader.GetStringOrEmpty(6)}.{reader.GetString(7)}',\n");
            }
        }
        else
        {
            job.Append("   start_date         =>  SYSDATE,\n");
            job.Append($"   repeat_interval    =>  '{Escape(reader.GetString(8))}',\n");
            job.Append("   end_date           =>  NULL,\n");
        }

        job.Append($"   enabled            =>  {reader.GetStringOrEmpty(9)},\n");
        job.Append($"   comments           =>  '{Escape(reader.GetStringOrEmpty(10))}');\n");
        job.Append("END;\n/\n\n");
        return job.ToString();
    }

    private async Task<string> ExtractSynonymsAsync(string owner, string name, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT OWNER, SYNONYM_NAME
            FROM ALL_SYNONYMS
            WHERE TABLE_OWNER = :owner
              AND TABLE_NAME = :objectName
            ORDER BY OWNER, SYNONYM_NAME
            """;

        await using var command = Command(sql, ("owner", owner), ("objectName", name));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var synonyms = new StringBuilder();
        while (await reader.ReadAsync(cancellationToken))
        {
            var synonymOwner = reader.GetString(0);
            var synonymName = reader.GetString(1);
            synonyms.Append(synonymOwner == "PUBLIC"
                ? $"CREATE OR REPLACE PUBLIC SYNONYM {synonymName} FOR {owner}.{name}\n/\n\n"
                : $"CREATE OR REPLACE SYNONYM {synonymOwner}.{synonymName} FOR {owner}.{name}\n/\n\n");
        }

        return synonyms.ToString();
    }

    private async Task<string> ExtractGrantsAsync(string owner, string name, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT GRANTEE, PRIVILEGE, GRANTABLE
            FROM ALL_TAB_PRIVS
            WHERE TABLE_SCHEMA = :owner
              AND TABLE_NAME = :objectName
            ORDER BY GRANTEE, PRIVILEGE
            """;

        await using var command = Command(sql, ("owner", owner), ("objectName", name));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var privileges = new List<(string Grantee, string Privilege, bool Grantable)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            privileges.Add((reader.GetString(0), reader.GetString(1), reader.GetStringOrEmpty(2) == "YES"));
        }

        var grants = new StringBuilder();
        foreach (var byGrantee in privileges.GroupBy(p => p.Grantee))
        {
            foreach (var group in byGrantee.GroupBy(p => p.Grantable).OrderBy(g => g.Key))
            {
                var list = string.Join(", ", group.Select(p => p.Privilege).Distinct());
                var suffix = group.Key ? " WITH GRANT OPTION" : "";
                grants.Append($"GRANT {list} ON {owner}.{name} TO {byGrantee.Key}{suffix}\n/\n\n");
            }
        }

        return grants.ToString();
    }

    // ---------- Tabela ----------

    private async Task<string> ExtractTableAsync(string owner, string name, bool inlineErrors, CancellationToken cancellationToken)
    {
        var columns = await ReadTableColumnsAsync(owner, name, cancellationToken);
        if (columns.Count == 0)
        {
            return "";
        }

        var constraints = await ReadTableConstraintsAsync(owner, name, cancellationToken);
        var constraintColumns = await ReadConstraintColumnsAsync(owner, name, cancellationToken);

        // CHECK "COL" IS NOT NULL habilitado vira NOT NULL na própria coluna (com o nome, quando não é gerado pelo sistema).
        var notNullNames = new Dictionary<string, string?>();
        var otherConstraints = new List<TableConstraint>();
        foreach (var constraint in constraints)
        {
            var match = constraint.Type == "C" && constraint.Status == "ENABLED"
                ? NotNullConditionRegex().Match(constraint.Condition)
                : Match.Empty;
            if (match.Success)
            {
                notNullNames[match.Groups["col"].Value] = constraint.GeneratedName ? null : constraint.Name;
            }
            else
            {
                otherConstraints.Add(constraint);
            }
        }

        var table = $"{owner}.{name}";
        var ddl = new StringBuilder();
        ddl.Append(await BuildCreateTableAsync(owner, name, columns, notNullNames, cancellationToken));
        ddl.Append(await ExtractTableCommentsAsync(owner, name, table, cancellationToken));
        ddl.Append(await BuildConstraintsAsync(table, otherConstraints, constraintColumns, cancellationToken));
        ddl.Append(await ExtractIndexesAsync(owner, name, table, cancellationToken));
        ddl.Append(await ExtractTableTriggersAsync(owner, name, inlineErrors, cancellationToken));
        return ddl.ToString();
    }

    private async Task<List<TableColumn>> ReadTableColumnsAsync(string owner, string name, CancellationToken cancellationToken)
    {
        // IDENTITY_COLUMN e DEFAULT_ON_NULL só existem a partir do 12c; no 11g (ORA-00904) a consulta é refeita sem eles.
        const string sql12 = """
            SELECT COLUMN_NAME, DATA_TYPE, DATA_TYPE_OWNER, DATA_LENGTH, CHAR_LENGTH, CHAR_USED,
                   DATA_PRECISION, DATA_SCALE, NULLABLE, VIRTUAL_COLUMN, IDENTITY_COLUMN, DEFAULT_ON_NULL, DATA_DEFAULT
            FROM ALL_TAB_COLS
            WHERE OWNER = :owner
              AND TABLE_NAME = :objectName
              AND HIDDEN_COLUMN = 'NO'
            ORDER BY COLUMN_ID
            """;
        const string sql11 = """
            SELECT COLUMN_NAME, DATA_TYPE, DATA_TYPE_OWNER, DATA_LENGTH, CHAR_LENGTH, CHAR_USED,
                   DATA_PRECISION, DATA_SCALE, NULLABLE, VIRTUAL_COLUMN, 'NO', 'NO', DATA_DEFAULT
            FROM ALL_TAB_COLS
            WHERE OWNER = :owner
              AND TABLE_NAME = :objectName
              AND HIDDEN_COLUMN = 'NO'
            ORDER BY COLUMN_ID
            """;

        try
        {
            return await ReadTableColumnsAsync(sql12, owner, name, cancellationToken);
        }
        catch (OracleException ex) when (ex.Number == 904)
        {
            return await ReadTableColumnsAsync(sql11, owner, name, cancellationToken);
        }
    }

    private async Task<List<TableColumn>> ReadTableColumnsAsync(string sql, string owner, string name, CancellationToken cancellationToken)
    {
        await using var command = Command(sql, ("owner", owner), ("objectName", name));
        command.InitialLONGFetchSize = -1;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var columns = new List<TableColumn>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var type = FormatDataType(
                reader.GetString(1),
                reader.GetStringOrEmpty(2),
                reader.GetNumberText(3),
                reader.GetNumberText(4),
                reader.GetStringOrEmpty(5),
                reader.GetNumberText(6),
                reader.GetNumberText(7));

            columns.Add(new TableColumn(
                reader.GetString(0),
                type,
                reader.GetStringOrEmpty(8),
                reader.GetStringOrEmpty(9),
                reader.GetStringOrEmpty(10),
                reader.GetStringOrEmpty(11),
                reader.GetStringOrEmpty(12).Trim()));
        }

        return columns;
    }

    private static string FormatDataType(
        string dataType, string typeOwner, string dataLength, string charLength, string charUsed, string precision, string scale)
    {
        return dataType switch
        {
            "VARCHAR2" or "CHAR" => $"{dataType}({charLength} {(charUsed == "C" ? "CHAR" : "BYTE")})",
            "NVARCHAR2" or "NCHAR" => $"{dataType}({charLength})",
            "RAW" or "UROWID" => $"{dataType}({dataLength})",
            "NUMBER" => (precision, scale) switch
            {
                ("", "") => "NUMBER",
                ("", _) => $"NUMBER(*,{scale})",
                (_, "" or "0") => $"NUMBER({precision})",
                _ => $"NUMBER({precision},{scale})"
            },
            "FLOAT" => precision.Length == 0 ? "FLOAT" : $"FLOAT({precision})",
            _ when typeOwner.Length > 0 && typeOwner is not ("SYS" or "PUBLIC") => $"{typeOwner}.{dataType}",
            _ => dataType
        };
    }

    private async Task<string> BuildCreateTableAsync(
        string owner, string name, List<TableColumn> columns, Dictionary<string, string?> notNullNames, CancellationToken cancellationToken)
    {
        const string tableSql = "SELECT TEMPORARY, DURATION FROM ALL_TABLES WHERE OWNER = :owner AND TABLE_NAME = :objectName";
        var temporary = false;
        var duration = "";
        await using (var command = Command(tableSql, ("owner", owner), ("objectName", name)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                temporary = reader.GetStringOrEmpty(0) == "Y";
                duration = reader.GetStringOrEmpty(1);
            }
        }

        var identityTypes = columns.Any(c => c.Identity == "YES")
            ? await ReadIdentityGenerationTypesAsync(owner, name, cancellationToken)
            : new Dictionary<string, string>();

        var lines = new List<string>();
        foreach (var column in columns)
        {
            var line = new StringBuilder($"   {OracleDdlExtensions.QuoteIfNeeded(column.Name)} {column.Type}");

            if (column.Virtual == "YES")
            {
                line.Append($" GENERATED ALWAYS AS ({column.Default}) VIRTUAL");
            }
            else if (column.Identity == "YES")
            {
                var generation = identityTypes.TryGetValue(column.Name, out var g) ? g : "BY DEFAULT";
                line.Append($" GENERATED {generation}{(column.DefaultOnNull == "YES" ? " ON NULL" : "")} AS IDENTITY");
            }
            else if (column.Default.Length > 0)
            {
                line.Append(column.DefaultOnNull == "YES" ? $" DEFAULT ON NULL {column.Default}" : $" DEFAULT {column.Default}");
            }

            if (column.Nullable == "N" && column.Identity != "YES")
            {
                line.Append(notNullNames.TryGetValue(column.Name, out var constraintName) && constraintName is not null
                    ? $" CONSTRAINT {OracleDdlExtensions.QuoteIfNeeded(constraintName)} NOT NULL"
                    : " NOT NULL");
            }

            lines.Add(line.ToString());
        }

        var ddl = new StringBuilder()
            .Append(temporary ? $"CREATE GLOBAL TEMPORARY TABLE {owner}.{name}\n(\n" : $"CREATE TABLE {owner}.{name}\n(\n")
            .Append(string.Join(",\n", lines))
            .Append("\n)");

        if (temporary)
        {
            ddl.Append(duration == "SYS$SESSION" ? "\nON COMMIT PRESERVE ROWS" : "\nON COMMIT DELETE ROWS");
        }

        return ddl.Append("\n/\n\n").ToString();
    }

    private async Task<Dictionary<string, string>> ReadIdentityGenerationTypesAsync(string owner, string name, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COLUMN_NAME, GENERATION_TYPE
            FROM ALL_TAB_IDENTITY_COLS
            WHERE OWNER = :owner
              AND TABLE_NAME = :objectName
            """;

        await using var command = Command(sql, ("owner", owner), ("objectName", name));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var types = new Dictionary<string, string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            types[reader.GetString(0)] = reader.GetStringOrEmpty(1);
        }

        return types;
    }

    private async Task<string> ExtractTableCommentsAsync(string owner, string name, string table, CancellationToken cancellationToken)
    {
        var ddl = new StringBuilder();

        const string tableSql = """
            SELECT COMMENTS
            FROM ALL_TAB_COMMENTS
            WHERE OWNER = :owner
              AND TABLE_NAME = :objectName
              AND COMMENTS IS NOT NULL
            """;
        await using (var command = Command(tableSql, ("owner", owner), ("objectName", name)))
        {
            if (await command.ExecuteScalarAsync(cancellationToken) is string comment)
            {
                ddl.Append($"COMMENT ON TABLE {table} IS '{Escape(comment)}'\n/\n\n");
            }
        }

        const string columnsSql = """
            SELECT CC.COLUMN_NAME, CC.COMMENTS
            FROM ALL_COL_COMMENTS CC
                JOIN ALL_TAB_COLUMNS TC
                ON TC.OWNER = CC.OWNER
                AND TC.TABLE_NAME = CC.TABLE_NAME
                AND TC.COLUMN_NAME = CC.COLUMN_NAME
            WHERE CC.OWNER = :owner
              AND CC.TABLE_NAME = :objectName
              AND CC.COMMENTS IS NOT NULL
            ORDER BY TC.COLUMN_ID
            """;
        await using (var command = Command(columnsSql, ("owner", owner), ("objectName", name)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                ddl.Append($"COMMENT ON COLUMN {table}.{OracleDdlExtensions.QuoteIfNeeded(reader.GetString(0))} IS '{Escape(reader.GetString(1))}'\n/\n\n");
            }
        }

        return ddl.ToString();
    }

    private async Task<List<TableConstraint>> ReadTableConstraintsAsync(string owner, string name, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CONSTRAINT_NAME, CONSTRAINT_TYPE, R_OWNER, R_CONSTRAINT_NAME, DELETE_RULE, STATUS,
                   DEFERRABLE, DEFERRED, VALIDATED, GENERATED, SEARCH_CONDITION
            FROM ALL_CONSTRAINTS
            WHERE OWNER = :owner
              AND TABLE_NAME = :objectName
              AND CONSTRAINT_TYPE IN ('P', 'U', 'R', 'C')
            ORDER BY DECODE(CONSTRAINT_TYPE, 'P', 1, 'U', 2, 'R', 3, 4), CONSTRAINT_NAME
            """;

        await using var command = Command(sql, ("owner", owner), ("objectName", name));
        command.InitialLONGFetchSize = -1;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var constraints = new List<TableConstraint>();
        while (await reader.ReadAsync(cancellationToken))
        {
            constraints.Add(new TableConstraint(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetStringOrEmpty(2),
                reader.GetStringOrEmpty(3),
                reader.GetStringOrEmpty(4),
                reader.GetStringOrEmpty(5),
                reader.GetStringOrEmpty(6),
                reader.GetStringOrEmpty(7),
                reader.GetStringOrEmpty(8),
                reader.GetStringOrEmpty(9) == "GENERATED NAME",
                reader.GetStringOrEmpty(10).Trim()));
        }

        return constraints;
    }

    private async Task<Dictionary<string, List<string>>> ReadConstraintColumnsAsync(string owner, string name, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CONSTRAINT_NAME, COLUMN_NAME
            FROM ALL_CONS_COLUMNS
            WHERE OWNER = :owner
              AND TABLE_NAME = :objectName
            ORDER BY CONSTRAINT_NAME, POSITION
            """;

        await using var command = Command(sql, ("owner", owner), ("objectName", name));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var columns = new Dictionary<string, List<string>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var constraintName = reader.GetString(0);
            if (!columns.TryGetValue(constraintName, out var list))
            {
                list = [];
                columns[constraintName] = list;
            }

            list.Add(OracleDdlExtensions.QuoteIfNeeded(reader.GetString(1)));
        }

        return columns;
    }

    private async Task<string> BuildConstraintsAsync(
        string table, List<TableConstraint> constraints, Dictionary<string, List<string>> constraintColumns, CancellationToken cancellationToken)
    {
        var ddl = new StringBuilder();
        foreach (var constraint in constraints)
        {
            var columns = string.Join(", ", constraintColumns.GetValueOrDefault(constraint.Name) ?? []);
            var statement = new StringBuilder($"ALTER TABLE {table} ADD ");
            if (!constraint.GeneratedName)
            {
                statement.Append($"CONSTRAINT {OracleDdlExtensions.QuoteIfNeeded(constraint.Name)} ");
            }

            switch (constraint.Type)
            {
                case "P":
                    statement.Append($"PRIMARY KEY ({columns})");
                    break;
                case "U":
                    statement.Append($"UNIQUE ({columns})");
                    break;
                case "C":
                    statement.Append($"CHECK ({constraint.Condition})");
                    break;
                case "R":
                    var reference = await ReadReferencedKeyAsync(constraint.ROwner, constraint.RName, cancellationToken);
                    if (reference is null)
                    {
                        ddl.Append($"-- FOREIGN KEY {constraint.Name}: constraint referenciada {constraint.ROwner}.{constraint.RName} não está acessível\n\n");
                        continue;
                    }

                    statement.Append($"FOREIGN KEY ({columns}) REFERENCES {constraint.ROwner}.{reference.Value.Table} ({reference.Value.Columns})");
                    if (constraint.DeleteRule is "CASCADE" or "SET NULL")
                    {
                        statement.Append($" ON DELETE {constraint.DeleteRule}");
                    }

                    break;
            }

            if (constraint.Deferrable == "DEFERRABLE")
            {
                statement.Append(constraint.Deferred == "DEFERRED" ? " DEFERRABLE INITIALLY DEFERRED" : " DEFERRABLE");
            }

            if (constraint.Status == "DISABLED")
            {
                statement.Append(" DISABLE");
            }
            else if (constraint.Validated == "NOT VALIDATED")
            {
                statement.Append(" ENABLE NOVALIDATE");
            }

            ddl.Append(statement).Append("\n/\n\n");
        }

        return ddl.ToString();
    }

    private async Task<(string Table, string Columns)?> ReadReferencedKeyAsync(string owner, string constraintName, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT C.TABLE_NAME, CC.COLUMN_NAME
            FROM ALL_CONSTRAINTS C
                JOIN ALL_CONS_COLUMNS CC
                ON CC.OWNER = C.OWNER
                AND CC.CONSTRAINT_NAME = C.CONSTRAINT_NAME
            WHERE C.OWNER = :owner
              AND C.CONSTRAINT_NAME = :constraintName
            ORDER BY CC.POSITION
            """;

        await using var command = Command(sql, ("owner", owner), ("constraintName", constraintName));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        string? table = null;
        var columns = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            table ??= reader.GetString(0);
            columns.Add(OracleDdlExtensions.QuoteIfNeeded(reader.GetString(1)));
        }

        return table is null ? null : (table, string.Join(", ", columns));
    }

    private async Task<string> ExtractIndexesAsync(string owner, string name, string table, CancellationToken cancellationToken)
    {
        // Índices que sustentam PK/UK são criados pela própria constraint; LOB, IOT e DOMAIN não se recriam com CREATE INDEX simples.
        const string indexesSql = """
            SELECT I.OWNER, I.INDEX_NAME, I.INDEX_TYPE, I.UNIQUENESS
            FROM ALL_INDEXES I
            WHERE I.TABLE_OWNER = :owner
              AND I.TABLE_NAME = :objectName
              AND I.INDEX_TYPE NOT IN ('LOB', 'IOT - TOP', 'DOMAIN')
              AND NOT EXISTS (
                  SELECT 1
                  FROM ALL_CONSTRAINTS C
                  WHERE C.OWNER = :owner
                    AND C.TABLE_NAME = :objectName
                    AND C.CONSTRAINT_TYPE IN ('P', 'U')
                    AND C.INDEX_OWNER = I.OWNER
                    AND C.INDEX_NAME = I.INDEX_NAME)
            ORDER BY I.OWNER, I.INDEX_NAME
            """;

        var indexes = new List<(string Owner, string Name, string Type, string Uniqueness)>();
        await using (var command = Command(indexesSql, ("owner", owner), ("objectName", name)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                indexes.Add((reader.GetString(0), reader.GetString(1), reader.GetStringOrEmpty(2), reader.GetStringOrEmpty(3)));
            }
        }

        if (indexes.Count == 0)
        {
            return "";
        }

        // Em índices por função (e colunas DESC) o nome da coluna é SYS_NC...; a expressão real vem de ALL_IND_EXPRESSIONS.
        const string columnsSql = """
            SELECT IC.INDEX_OWNER, IC.INDEX_NAME, IC.COLUMN_NAME, IC.DESCEND, IE.COLUMN_EXPRESSION
            FROM ALL_IND_COLUMNS IC
                LEFT JOIN ALL_IND_EXPRESSIONS IE
                ON IE.INDEX_OWNER = IC.INDEX_OWNER
                AND IE.INDEX_NAME = IC.INDEX_NAME
                AND IE.COLUMN_POSITION = IC.COLUMN_POSITION
            WHERE IC.TABLE_OWNER = :owner
              AND IC.TABLE_NAME = :objectName
            ORDER BY IC.INDEX_OWNER, IC.INDEX_NAME, IC.COLUMN_POSITION
            """;

        var indexColumns = new Dictionary<(string, string), List<string>>();
        await using (var command = Command(columnsSql, ("owner", owner), ("objectName", name)))
        {
            command.InitialLONGFetchSize = -1;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!indexColumns.TryGetValue(key, out var list))
                {
                    list = [];
                    indexColumns[key] = list;
                }

                var expression = reader.GetStringOrEmpty(4).Trim();
                var column = expression.Length > 0 ? expression : OracleDdlExtensions.QuoteIfNeeded(reader.GetString(2));
                list.Add(reader.GetStringOrEmpty(3) == "DESC" ? column + " DESC" : column);
            }
        }

        var ddl = new StringBuilder();
        foreach (var index in indexes)
        {
            var kind = index.Uniqueness == "UNIQUE" ? "UNIQUE " : index.Type.Contains("BITMAP") ? "BITMAP " : "";
            var columns = string.Join(", ", indexColumns.GetValueOrDefault((index.Owner, index.Name)) ?? []);
            ddl.Append($"CREATE {kind}INDEX {index.Owner}.{OracleDdlExtensions.QuoteIfNeeded(index.Name)} ON {table} ({columns})\n/\n\n");
        }

        return ddl.ToString();
    }

    private async Task<string> ExtractTableTriggersAsync(string owner, string name, bool inlineErrors, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT OWNER, TRIGGER_NAME
            FROM ALL_TRIGGERS
            WHERE TABLE_OWNER = :owner
              AND TABLE_NAME = :objectName
              AND BASE_OBJECT_TYPE = 'TABLE'
            ORDER BY OWNER, TRIGGER_NAME
            """;

        var triggers = new List<(string Owner, string Name)>();
        await using (var command = Command(sql, ("owner", owner), ("objectName", name)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                triggers.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var ddl = new StringBuilder();
        foreach (var trigger in triggers)
        {
            ddl.Append(await ExtractStoredSourceAsync(trigger.Owner, trigger.Name, "TRIGGER", inlineErrors, cancellationToken));
        }

        return ddl.ToString();
    }

    private static string Escape(string value) => value.Replace("'", "''");

    [GeneratedRegex(" {2,}")]
    private static partial Regex MultipleSpacesRegex();

    [GeneratedRegex(@"\b(BEGIN|DECLARE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BeginOrDeclareRegex();

    [GeneratedRegex(@"^\s*""(?<col>[^""]+)""\s+IS\s+NOT\s+NULL\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex NotNullConditionRegex();
}
