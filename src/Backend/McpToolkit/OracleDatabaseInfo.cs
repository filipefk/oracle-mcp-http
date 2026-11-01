using McpToolkit.Ddl;
using Oracle.ManagedDataAccess.Client;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace McpToolkit;

// Portado de OracleTools2026.Core\Data\DatabaseObjectRepository.GetDatabaseDetailsAsync.
// Cada consulta é independente: sem privilégio na view (V$*, DBA_*) ou em versão antiga, a informação é omitida.
internal sealed class OracleDatabaseInfo(OracleConnection connection, int commandTimeoutSeconds)
{
    public async Task<JsonObject> GetDatabaseDetailsAsync(CancellationToken cancellationToken)
    {
        const string databaseGroup = "Banco de dados";
        const string instanceGroup = "Instância";
        const string storageGroup = "Armazenamento";
        const string nlsGroup = "NLS";
        const string sessionGroup = "Sessão";
        const string clientGroup = "Cliente";

        var result = new JsonObject();

        void Add(string group, string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (result[group] is not JsonObject properties)
            {
                properties = new JsonObject();
                result[group] = properties;
            }

            properties[name] = value.Trim();
        }

        // VERSION_FULL e CON_NAME só existem a partir do 18c/12c; em versões anteriores a consulta falha e cai no fallback.
        var version = await TryReadRowAsync("SELECT VERSION_FULL FROM PRODUCT_COMPONENT_VERSION WHERE PRODUCT LIKE 'Oracle%' AND ROWNUM = 1", cancellationToken)
            ?? await TryReadRowAsync("SELECT VERSION FROM PRODUCT_COMPONENT_VERSION WHERE PRODUCT LIKE 'Oracle%' AND ROWNUM = 1", cancellationToken);
        Add(databaseGroup, "Versão", version?[0]);

        var banner = await TryReadRowAsync("SELECT BANNER FROM V$VERSION WHERE BANNER LIKE 'Oracle%' AND ROWNUM = 1", cancellationToken);
        Add(databaseGroup, "Produto", banner?[0]);

        const string contextSql = """
            SELECT SYS_CONTEXT('USERENV', 'DB_NAME'),
                   SYS_CONTEXT('USERENV', 'DB_UNIQUE_NAME'),
                   SYS_CONTEXT('USERENV', 'SERVICE_NAME'),
                   SYS_CONTEXT('USERENV', 'INSTANCE_NAME'),
                   SYS_CONTEXT('USERENV', 'SERVER_HOST'),
                   DBTIMEZONE,
                   TO_CHAR(SYSTIMESTAMP, 'DD/MM/YYYY HH24:MI:SS TZH:TZM')
            FROM DUAL
            """;
        if (await TryReadRowAsync(contextSql, cancellationToken) is { } context)
        {
            Add(databaseGroup, "Nome", context[0]);
            Add(databaseGroup, "Nome único", context[1]);
            Add(databaseGroup, "Serviço", context[2]);
            Add(databaseGroup, "Instância", context[3]);
            Add(databaseGroup, "Servidor", context[4]);
            Add(databaseGroup, "Fuso horário", context[5]);
            Add(databaseGroup, "Data/hora do servidor", context[6]);
        }

        var container = await TryReadRowAsync("SELECT SYS_CONTEXT('USERENV', 'CON_NAME') FROM DUAL", cancellationToken);
        Add(databaseGroup, "Container (PDB)", container?[0]);

        const string databaseSql = """
            SELECT TO_CHAR(CREATED, 'DD/MM/YYYY HH24:MI:SS'), LOG_MODE, OPEN_MODE, DATABASE_ROLE, PLATFORM_NAME
            FROM V$DATABASE
            """;
        if (await TryReadRowAsync(databaseSql, cancellationToken) is { } database)
        {
            Add(databaseGroup, "Criado em", database[0]);
            Add(databaseGroup, "Modo de log", database[1]);
            Add(databaseGroup, "Modo de abertura", database[2]);
            Add(databaseGroup, "Papel", database[3]);
            Add(databaseGroup, "Plataforma", database[4]);
        }

        const string instanceSql = """
            SELECT HOST_NAME,
                   STATUS,
                   TO_CHAR(STARTUP_TIME, 'DD/MM/YYYY HH24:MI:SS'),
                   TRUNC(SYSDATE - STARTUP_TIME) || ' dia(s) e ' || TRUNC(MOD((SYSDATE - STARTUP_TIME) * 24, 24)) || ' hora(s)'
            FROM V$INSTANCE
            """;
        if (await TryReadRowAsync(instanceSql, cancellationToken) is { } instance)
        {
            Add(instanceGroup, "Host", instance[0]);
            Add(instanceGroup, "Status", instance[1]);
            Add(instanceGroup, "Iniciada em", instance[2]);
            Add(instanceGroup, "Tempo no ar", instance[3]);
        }

        const string storageSql = """
            SELECT TO_CHAR((SELECT SUM(BYTES) FROM DBA_DATA_FILES) / 1073741824, 'FM999G999G990D00', 'NLS_NUMERIC_CHARACTERS='',.''') || ' GB',
                   TO_CHAR((SELECT SUM(BYTES) FROM DBA_SEGMENTS) / 1073741824, 'FM999G999G990D00', 'NLS_NUMERIC_CHARACTERS='',.''') || ' GB'
            FROM DUAL
            """;
        if (await TryReadRowAsync(storageSql, cancellationToken) is { } storage)
        {
            Add(storageGroup, "Alocado (datafiles)", storage[0]);
            Add(storageGroup, "Utilizado (segmentos)", storage[1]);
        }

        const string nlsSql = """
            SELECT MAX(CASE PARAMETER WHEN 'NLS_CHARACTERSET' THEN VALUE END),
                   MAX(CASE PARAMETER WHEN 'NLS_NCHAR_CHARACTERSET' THEN VALUE END),
                   MAX(CASE PARAMETER WHEN 'NLS_LANGUAGE' THEN VALUE END),
                   MAX(CASE PARAMETER WHEN 'NLS_TERRITORY' THEN VALUE END)
            FROM NLS_DATABASE_PARAMETERS
            """;
        if (await TryReadRowAsync(nlsSql, cancellationToken) is { } nls)
        {
            Add(nlsGroup, "Character set", nls[0]);
            Add(nlsGroup, "National character set", nls[1]);
            Add(nlsGroup, "Idioma", nls[2]);
            Add(nlsGroup, "Território", nls[3]);
        }

        const string sessionSql = """
            SELECT SYS_CONTEXT('USERENV', 'SESSION_USER'),
                   SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA'),
                   SYS_CONTEXT('USERENV', 'SID'),
                   SYS_CONTEXT('USERENV', 'OS_USER'),
                   SYS_CONTEXT('USERENV', 'TERMINAL'),
                   SYS_CONTEXT('USERENV', 'IP_ADDRESS'),
                   SESSIONTIMEZONE
            FROM DUAL
            """;
        if (await TryReadRowAsync(sessionSql, cancellationToken) is { } sessionInfo)
        {
            Add(sessionGroup, "Usuário", sessionInfo[0]);
            Add(sessionGroup, "Schema atual", sessionInfo[1]);
            Add(sessionGroup, "SID", sessionInfo[2]);
            Add(sessionGroup, "Usuário do SO", sessionInfo[3]);
            Add(sessionGroup, "Terminal", sessionInfo[4]);
            Add(sessionGroup, "Endereço IP", sessionInfo[5]);
            Add(sessionGroup, "Fuso horário", sessionInfo[6]);
        }

        Add(sessionGroup, "Banco", connection.DataSource);
        var privilege = await TryReadRowAsync("SELECT SYS_CONTEXT('USERENV', 'ISDBA') FROM DUAL", cancellationToken);
        if (privilege?[0] == "TRUE")
        {
            Add(sessionGroup, "Privilégio", "SYSDBA");
        }

        Add(clientGroup, "Versão do servidor (driver)", connection.ServerVersion);
        Add(clientGroup, "Oracle.ManagedDataAccess", typeof(OracleConnection).Assembly.GetName().Version?.ToString());
        Add(clientGroup, "OracleMcpHttp", (Assembly.GetEntryAssembly() ?? typeof(OracleDatabaseInfo).Assembly).GetName().Version?.ToString(3));
        Add(clientGroup, ".NET", RuntimeInformation.FrameworkDescription);

        return result;
    }

    private async Task<string[]?> TryReadRowAsync(string sql, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateTextCommand(sql);
            if (commandTimeoutSeconds > 0)
            {
                command.CommandTimeout = commandTimeoutSeconds;
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var values = new string[reader.FieldCount];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = reader.GetStringOrEmpty(i);
            }

            return values;
        }
        catch (OracleException)
        {
            // Sem privilégio na view (ex.: V$INSTANCE, DBA_*) ou recurso inexistente na versão do banco: a informação é omitida.
            return null;
        }
    }
}
