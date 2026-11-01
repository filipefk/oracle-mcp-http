using McpToolkit.Catalog;
using McpToolkit.Ddl;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace McpToolkit;

// Uma conexão Oracle aberta por sessão MCP (header Mcp-Session-Id): como cada cliente pode trazer
// credenciais próprias na API Key, uma conexão única compartilhada misturaria usuários do banco.
// Cada sessão serializa o acesso à sua conexão com um SemaphoreSlim (OracleConnection não é thread-safe).
public sealed class OracleConnectionManager(
    ILogger<OracleConnectionManager> logger,
    IOptions<McpOptions> options,
    IRequestContext requestContext) : IAsyncDisposable
{
    private const string DefaultSessionKey = "default";

    private readonly ConcurrentDictionary<string, OracleSession> _sessions = new();

    private sealed class OracleSession
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public OracleConnection? Connection { get; set; }
        public bool Removed { get; set; }
        private long _lastActivityTicks = DateTime.UtcNow.Ticks;

        public TimeSpan IdleTime => DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

        public void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
    }

    public int ActiveConnections => _sessions.Count(s => s.Value.Connection is not null);

    private string SessionKey => string.IsNullOrWhiteSpace(requestContext.SessionId) ? DefaultSessionKey : requestContext.SessionId;

    // Ordem: connection string da API Key > configuração do servidor > parâmetro da tool.
    private (string ConnectionString, string Source) ResolveConnectionString(string? parameterConnectionString)
    {
        if (!string.IsNullOrWhiteSpace(requestContext.OracleConnectionString))
            return (requestContext.OracleConnectionString, "api-key");

        if (!string.IsNullOrWhiteSpace(options.Value.ConnectionString))
            return (options.Value.ConnectionString, "configuracao-servidor");

        if (!string.IsNullOrWhiteSpace(parameterConnectionString))
            return (parameterConnectionString, "parametro");

        throw new InvalidOperationException(
            "Nenhuma connection string disponível: a API Key não traz uma, o servidor não tem Oracle:ConnectionString configurada e o parâmetro connection_string não foi informado.");
    }

    public async Task<JsonObject> ConnectAsync(string? connectionString, CancellationToken ct)
    {
        var (effectiveConnectionString, source) = ResolveConnectionString(connectionString);

        var session = await LockSessionAsync(ct);
        try
        {
            await CloseConnectionAsync(session);

            var connection = new OracleConnection(effectiveConnectionString);
            try
            {
                await connection.OpenAsync(ct);
                var info = await DescribeAsync(connection, ct);
                info["source"] = source;

                session.Connection = connection;
                session.Touch();
                logger.LogInformation("Conexão Oracle aberta (sessão={Session}, database={Database}, user={User}, origem={Source}).",
                    SessionKey, info["database"]?.ToString(), info["user"]?.ToString(), source);
                return info;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
        finally
        {
            session.Lock.Release();
        }
    }

    public async Task<JsonObject> DisconnectAsync(CancellationToken ct)
    {
        var key = SessionKey;
        if (!_sessions.TryGetValue(key, out var session))
            return new JsonObject { ["disconnected"] = true, ["wasConnected"] = false };

        await session.Lock.WaitAsync(ct);
        try
        {
            var wasConnected = !session.Removed && session.Connection is not null;
            await RemoveSessionAsync(key, session);
            if (wasConnected)
                logger.LogInformation("Conexão Oracle fechada (sessão={Session}).", key);
            return new JsonObject { ["disconnected"] = true, ["wasConnected"] = wasConnected };
        }
        finally
        {
            session.Lock.Release();
        }
    }

    public async Task<JsonObject> ExecuteQueryAsync(string sql, JsonObject? parameters, int maxRows, CancellationToken ct)
    {
        var normalizedSql = OracleSql.NormalizeSql(sql);
        if (maxRows <= 0) maxRows = 1000;

        return await WithConnectionAsync(async connection =>
        {
            await using var cmd = CreateCommand(connection, normalizedSql, parameters);
            await using var reader = (OracleDataReader)await cmd.ExecuteReaderAsync(ct);

            var names = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                names[i] = reader.GetName(i);
            var columnNames = OracleSql.UniqueColumnNames(names);

            var columns = new JsonArray();
            for (var i = 0; i < reader.FieldCount; i++)
                columns.Add(new JsonObject
                {
                    ["name"] = columnNames[i],
                    ["type"] = reader.GetDataTypeName(i)
                });

            var rows = new JsonArray();
            var truncated = false;
            while (await reader.ReadAsync(ct))
            {
                if (rows.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }

                var row = new JsonObject();
                for (var i = 0; i < reader.FieldCount; i++)
                    row[columnNames[i]] = OracleSql.ReadValue(reader, i);
                rows.Add(row);
            }

            return new JsonObject
            {
                ["columns"] = columns,
                ["rows"] = rows,
                ["rowCount"] = rows.Count,
                ["truncated"] = truncated
            };
        }, ct);
    }

    public async Task<JsonObject> ExecuteCommandAsync(string sql, JsonObject? parameters, CancellationToken ct)
    {
        var normalizedSql = OracleSql.NormalizeSql(sql);

        return await WithConnectionAsync(async connection =>
        {
            await using var cmd = CreateCommand(connection, normalizedSql, parameters);
            var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);

            // Blocos PL/SQL e DDL não informam linhas afetadas (o ODP.NET devolve -1).
            return new JsonObject
            {
                ["rowsAffected"] = rowsAffected,
                ["isPlSql"] = OracleSql.IsPlSql(normalizedSql)
            };
        }, ct);
    }

    public async Task<JsonObject> ExtractDdlAsync(string objectName, bool includeHeader, bool inlineCompileErrors, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(objectName))
            throw new InvalidOperationException("O parâmetro 'object_name' não pode ser vazio.");

        return await WithConnectionAsync(async connection =>
        {
            var extractor = new OracleDdlExtractor(connection, options.Value.CommandTimeoutSeconds);
            var result = await extractor.ExtractAsync(objectName.Trim(), new DdlExtractionOptions(includeHeader, inlineCompileErrors), ct);
            if (result.Error is not null)
                throw new InvalidOperationException(result.Error);

            return new JsonObject
            {
                ["object"] = result.QualifiedName,
                ["objectType"] = result.ObjectType,
                ["ddl"] = result.Source
            };
        }, ct);
    }

    public async Task<JsonObject> ExtractUserScriptAsync(string userNames, CancellationToken ct)
    {
        var names = (userNames ?? "")
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.Replace("\"", "").ToUpperInvariant())
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();
        if (names.Count == 0)
            throw new InvalidOperationException("O parâmetro 'user_names' não pode ser vazio.");

        return await WithConnectionAsync(async connection =>
        {
            var generator = new OracleUserScriptGenerator(connection, options.Value.CommandTimeoutSeconds);
            var results = new List<UserScriptResult>(names.Count);
            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await generator.GenerateAsync(name, ct));
            }

            var failures = results.Where(r => !r.Succeeded).ToList();
            if (failures.Count == results.Count)
                throw new InvalidOperationException(string.Join("\n", failures.Select(f => f.Error)));

            return new JsonObject
            {
                ["users"] = new JsonArray(results.Where(r => r.Succeeded).Select(r => (JsonNode)r.UserName).ToArray()),
                ["errors"] = new JsonArray(failures.Select(f => (JsonNode)f.Error!).ToArray()),
                ["script"] = string.Join("\r\n\r\n", results.Where(r => r.Succeeded).Select(r => r.Script))
            };
        }, ct);
    }

    public async Task<JsonObject> GetDatabaseInfoAsync(CancellationToken ct) =>
        await WithConnectionAsync(connection =>
            new OracleDatabaseInfo(connection, options.Value.CommandTimeoutSeconds).GetDatabaseDetailsAsync(ct), ct);

    public async Task<JsonObject> ListObjectsAsync(string? mode, string? owner, string? objectType, string? name, int days, int maxRows, CancellationToken ct)
    {
        static string? NullIfAll(string? value) =>
            string.IsNullOrWhiteSpace(value) || value.Trim().Equals("Todos", StringComparison.OrdinalIgnoreCase) ? null : value.Trim();

        var normalizedMode = string.IsNullOrWhiteSpace(mode) ? "search" : mode.Trim().ToLowerInvariant();
        owner = NullIfAll(owner)?.Replace("\"", "");
        objectType = NullIfAll(objectType)?.ToUpperInvariant();
        name = NullIfAll(name);
        if (maxRows <= 0) maxRows = 1000;

        if (normalizedMode is not ("search" or "recent" or "invalid"))
            throw new InvalidOperationException("O parâmetro 'mode' deve ser 'search', 'recent' ou 'invalid'.");

        if (normalizedMode == "search" && objectType is not null && !OracleObjectRepository.SupportedTypes.Contains(objectType))
            throw new InvalidOperationException(
                $"Tipo '{objectType}' não suportado. Tipos válidos: {string.Join(", ", OracleObjectRepository.SupportedTypes)}.");

        // Como na tela: a listagem de recentes ignora os filtros de owner, tipo e nome.
        if (normalizedMode == "recent")
        {
            days = days <= 0 ? 15 : Math.Min(days, 3650);
            owner = objectType = name = null;
        }

        return await WithConnectionAsync(async connection =>
        {
            var repository = new OracleObjectRepository(connection, options.Value.CommandTimeoutSeconds);
            var objects = normalizedMode switch
            {
                "recent" => await repository.GetRecentlyCompiledAsync(days, ct),
                "invalid" => await repository.GetInvalidAsync(owner, objectType, name, ct),
                _ => await repository.SearchAsync(owner, objectType, name, ct)
            };

            var filters = new JsonObject();
            if (normalizedMode == "recent")
            {
                filters["days"] = days;
            }
            else
            {
                filters["owner"] = owner;
                filters["objectType"] = objectType;
                filters["name"] = name;
            }

            return new JsonObject
            {
                ["mode"] = normalizedMode,
                ["filters"] = filters,
                ["totalCount"] = objects.Count,
                ["invalidCount"] = objects.Count(o => o.Status == "INVALID"),
                ["truncated"] = objects.Count > maxRows,
                ["objects"] = new JsonArray(objects.Take(maxRows).Select(o => (JsonNode)o.ToJson()).ToArray())
            };
        }, ct);
    }

    public async Task<JsonObject> ListObjectOwnersAsync(CancellationToken ct) =>
        await WithConnectionAsync(async connection =>
        {
            var owners = await new OracleObjectRepository(connection, options.Value.CommandTimeoutSeconds).GetOwnersAsync(ct);
            return new JsonObject
            {
                ["count"] = owners.Count,
                ["owners"] = new JsonArray(owners.Select(o => (JsonNode)o).ToArray())
            };
        }, ct);

    public async Task<JsonObject> ListDatabaseLinksAsync(CancellationToken ct) =>
        await WithConnectionAsync(async connection =>
        {
            var links = await new OracleDatabaseLinkRepository(connection, options.Value.CommandTimeoutSeconds).GetLinksAsync(ct);
            return new JsonObject
            {
                ["count"] = links.Count,
                ["dbLinks"] = new JsonArray(links.Select(l => (JsonNode)l.ToJson()).ToArray())
            };
        }, ct);

    public async Task<JsonObject> TestDatabaseLinksAsync(string? linkNames, CancellationToken ct)
    {
        var requested = (linkNames ?? "")
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.Length > 0)
            .ToList();

        return await WithConnectionAsync(async connection =>
        {
            var repository = new OracleDatabaseLinkRepository(connection, options.Value.CommandTimeoutSeconds);
            var links = await repository.GetLinksAsync(ct);

            // Sem nomes informados testa todos ("Testar todos"); com nomes, só os informados ("Testar selecionados").
            // Aceita NOME ou OWNER.NOME; um nome repetido em vários owners (ex.: PUBLIC e outro usuário) testa todos eles.
            var selected = new List<DatabaseLink>();
            var notFound = new JsonArray();
            if (requested.Count == 0)
            {
                selected.AddRange(links);
            }
            else
            {
                foreach (var item in requested)
                {
                    var matches = links.Where(l =>
                        l.Name.Equals(item, StringComparison.OrdinalIgnoreCase) ||
                        $"{l.Owner}.{l.Name}".Equals(item, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 0)
                        notFound.Add(item);
                    foreach (var match in matches)
                    {
                        if (!selected.Contains(match))
                            selected.Add(match);
                    }
                }
            }

            var results = new JsonArray();
            var failures = 0;
            foreach (var link in selected)
            {
                ct.ThrowIfCancellationRequested();
                var error = await repository.TestLinkAsync(link.Name, ct);
                if (error is not null) failures++;

                var json = link.ToJson();
                json["ok"] = error is null;
                json["test"] = error is null ? "TESTE OK" : $"ERRO: {error.ReplaceLineEndings(" ")}";
                results.Add(json);
            }

            return new JsonObject
            {
                ["tested"] = selected.Count,
                ["ok"] = selected.Count - failures,
                ["failed"] = failures,
                ["notFound"] = notFound,
                ["dbLinks"] = results
            };
        }, ct);
    }

    public async Task<JsonObject> ListSchedulerJobsAsync(CancellationToken ct) =>
        await WithConnectionAsync(async connection =>
        {
            var jobs = await new OracleSchedulerJobRepository(connection, options.Value.CommandTimeoutSeconds).GetJobsAsync(ct);
            return new JsonObject
            {
                ["count"] = jobs.Count,
                ["summary"] = new JsonObject
                {
                    ["ok"] = jobs.Count(j => j.Health == SchedulerJobHealth.Ok),
                    ["disabled"] = jobs.Count(j => j.Health == SchedulerJobHealth.Disabled),
                    ["overdue"] = jobs.Count(j => j.Health == SchedulerJobHealth.Overdue),
                    ["longRunning"] = jobs.Count(j => j.Health == SchedulerJobHealth.LongRunning)
                },
                ["jobs"] = new JsonArray(jobs.Select(j => (JsonNode)j.ToJson()).ToArray())
            };
        }, ct);

    // Abre uma conexão avulsa só para validar a connection string (usado pela tela de geração da API Key).
    public static async Task<JsonObject> TestConnectionAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(ct);
        return await DescribeAsync(connection, ct);
    }

    private static async Task<JsonObject> DescribeAsync(OracleConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT SYS_CONTEXT('USERENV','DB_NAME'), SYS_CONTEXT('USERENV','SERVICE_NAME'), " +
            "SYS_CONTEXT('USERENV','SESSION_USER'), SYS_CONTEXT('USERENV','CURRENT_SCHEMA') FROM DUAL";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        string? Read(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

        return new JsonObject
        {
            ["connected"] = true,
            ["serverVersion"] = connection.ServerVersion,
            ["database"] = Read(0),
            ["serviceName"] = Read(1),
            ["user"] = Read(2),
            ["currentSchema"] = Read(3)
        };
    }

    private OracleCommand CreateCommand(OracleConnection connection, string sql, JsonObject? parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (options.Value.CommandTimeoutSeconds > 0)
            cmd.CommandTimeout = options.Value.CommandTimeoutSeconds;
        OracleSql.AddParameters(cmd, parameters);
        return cmd;
    }

    private async Task<JsonObject> WithConnectionAsync(Func<OracleConnection, Task<JsonObject>> action, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(SessionKey, out var session))
            throw NotConnected();

        await session.Lock.WaitAsync(ct);
        try
        {
            if (session.Removed || session.Connection is null)
                throw NotConnected();

            session.Touch();
            var result = await action(session.Connection);
            session.Touch();
            return result;
        }
        finally
        {
            session.Lock.Release();
        }
    }

    private static InvalidOperationException NotConnected() =>
        new("Nenhuma conexão ativa com o Oracle nesta sessão. Chame a ferramenta 'connect' antes.");

    // Devolve a sessão já travada. Se o fechamento por inatividade removeu a sessão entre o GetOrAdd
    // e o WaitAsync, tenta de novo com uma sessão nova — senão a conexão iria para um objeto órfão.
    private async Task<OracleSession> LockSessionAsync(CancellationToken ct)
    {
        while (true)
        {
            var session = _sessions.GetOrAdd(SessionKey, _ => new OracleSession());
            await session.Lock.WaitAsync(ct);
            if (!session.Removed)
                return session;
            session.Lock.Release();
        }
    }

    internal async Task CloseIdleAsync(TimeSpan timeout, CancellationToken ct)
    {
        foreach (var (key, session) in _sessions)
        {
            if (session.IdleTime < timeout) continue;

            // Sessão ocupada (consulta longa em andamento) não é inativa: tenta de novo no próximo ciclo.
            if (!await session.Lock.WaitAsync(0, ct)) continue;
            try
            {
                if (session.Removed || session.IdleTime < timeout) continue;
                var hadConnection = session.Connection is not null;
                await RemoveSessionAsync(key, session);
                if (hadConnection)
                    logger.LogInformation("Conexão Oracle fechada por inatividade (sessão={Session}, timeout={Timeout}).", key, timeout);
            }
            finally
            {
                session.Lock.Release();
            }
        }
    }

    private async Task RemoveSessionAsync(string key, OracleSession session)
    {
        await CloseConnectionAsync(session);
        session.Removed = true;
        _sessions.TryRemove(new KeyValuePair<string, OracleSession>(key, session));
    }

    private async Task CloseConnectionAsync(OracleSession session)
    {
        if (session.Connection is null) return;
        try
        {
            await session.Connection.CloseAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao fechar a conexão Oracle; descartando mesmo assim.");
        }
        await session.Connection.DisposeAsync();
        session.Connection = null;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            if (session.Connection is not null)
                await session.Connection.DisposeAsync();
        }
        _sessions.Clear();
    }
}
