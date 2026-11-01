using Oracle.ManagedDataAccess.Client;
using System.Text;

namespace McpToolkit.Ddl;

// Portado de OracleTools2026.Core\Data\UserScriptGenerator.cs (e das consultas de DatabaseUserRepository que ele usa).
// Gera o script de criação do usuário no mesmo formato do original: CREATE USER com senha a definir,
// cotas, privilégios de sistema, roles (com as roles default) e grants de objetos concedidos diretamente.
internal sealed class OracleUserScriptGenerator(OracleConnection connection, int commandTimeoutSeconds)
{
    private const string PasswordPlaceholder = "<SENHA_USUARIO>";
    private const string Separator = "--=======================================================================================================================";

    private const string ObjectTypeSql = """
        (SELECT MIN(O.OBJECT_TYPE)
         FROM DBA_OBJECTS O
         WHERE O.OWNER = G.OWNER
           AND O.OBJECT_NAME = G.TABLE_NAME
           AND O.SUBOBJECT_NAME IS NULL
           AND O.OBJECT_TYPE NOT IN ('PACKAGE BODY', 'TYPE BODY', 'INDEX', 'TRIGGER', 'LOB', 'TABLE PARTITION',
                                     'TABLE SUBPARTITION', 'INDEX PARTITION', 'INDEX SUBPARTITION', 'LOB PARTITION'))
        """;

    private sealed record TablespaceQuota(string Tablespace, long? MaxBytes)
    {
        public bool IsUnlimited => MaxBytes is null;
    }

    private sealed record SystemPrivilegeGrant(string Privilege, bool AdminOption);

    private sealed record RoleGrant(string Role, bool AdminOption, bool IsDefault);

    // Column é preenchida apenas nos privilégios concedidos em colunas específicas (DBA_COL_PRIVS).
    private sealed record ObjectPrivilegeGrant(string Owner, string ObjectName, string ObjectType, string Privilege, bool Grantable, string? Column)
    {
        public bool IsDirectory => ObjectType == "DIRECTORY";
    }

    public async Task<UserScriptResult> GenerateAsync(string userName, CancellationToken cancellationToken)
    {
        userName = userName.Trim().Replace("\"", "").ToUpperInvariant();
        try
        {
            var script = await BuildScriptAsync(userName, cancellationToken);
            return script is null
                ? UserScriptResult.Failure(userName, $"Usuário {userName} não encontrado")
                : new UserScriptResult(userName, script, null);
        }
        catch (OracleException ex)
        {
            return UserScriptResult.Failure(userName, $"Usuário {userName}: {ex.Message}");
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

    private async Task<string?> BuildScriptAsync(string userName, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ACCOUNT_STATUS, DEFAULT_TABLESPACE, TEMPORARY_TABLESPACE, PROFILE
            FROM DBA_USERS
            WHERE USERNAME = :userName
            """;

        string status, defaultTablespace, temporaryTablespace, profile;
        await using (var command = Command(sql, ("userName", userName)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            status = reader.GetStringOrEmpty(0);
            defaultTablespace = reader.GetStringOrEmpty(1);
            temporaryTablespace = reader.GetStringOrEmpty(2);
            profile = reader.GetStringOrEmpty(3);
        }

        var user = OracleDdlExtensions.QuoteIfNeeded(userName);
        var script = new StringBuilder();
        script.Append("CREATE USER ").AppendLine(user);
        script.Append("IDENTIFIED BY ").AppendLine(PasswordPlaceholder);
        AppendClause(script, "DEFAULT TABLESPACE", defaultTablespace);
        AppendClause(script, "TEMPORARY TABLESPACE", temporaryTablespace);
        AppendClause(script, "PROFILE", profile);

        foreach (var quota in await GetQuotasAsync(userName, cancellationToken))
        {
            var size = quota.IsUnlimited ? "UNLIMITED" : quota.MaxBytes.ToString();
            script.AppendLine($"QUOTA {size} ON {OracleDdlExtensions.QuoteIfNeeded(quota.Tablespace)}");
        }

        // LOCKED(TIMED) é o bloqueio temporário por tentativas de login e não é reproduzido.
        var statusParts = status.Split('&', StringSplitOptions.TrimEntries);
        if (statusParts.Contains("LOCKED"))
        {
            script.AppendLine("ACCOUNT LOCK");
        }

        if (statusParts.Contains("EXPIRED"))
        {
            script.AppendLine("PASSWORD EXPIRE");
        }

        script.AppendLine("/");

        foreach (var privilege in await GetSystemPrivilegesAsync(userName, cancellationToken))
        {
            AppendStatement(script, $"GRANT {privilege.Privilege} TO {user}{(privilege.AdminOption ? " WITH ADMIN OPTION" : "")}");
        }

        var roles = await GetRolesAsync(userName, cancellationToken);
        foreach (var role in roles)
        {
            AppendStatement(script, $"GRANT {OracleDdlExtensions.QuoteIfNeeded(role.Role)} TO {user}{(role.AdminOption ? " WITH ADMIN OPTION" : "")}");
        }

        // Com pelo menos uma role não default, tira o default de todas e define só as que são default.
        if (roles.Any(r => !r.IsDefault))
        {
            AppendStatement(script, $"ALTER USER {user} DEFAULT ROLE NONE");

            var defaultRoles = roles.Where(r => r.IsDefault).Select(r => OracleDdlExtensions.QuoteIfNeeded(r.Role)).ToList();
            if (defaultRoles.Count > 0)
            {
                AppendStatement(script, $"ALTER USER {user} DEFAULT ROLE {string.Join(", ", defaultRoles)}");
            }
        }

        var objectGrants = await GetObjectPrivilegesAsync(userName, cancellationToken);
        if (objectGrants.Count > 0)
        {
            script.AppendLine();
            script.AppendLine(Separator);
            script.AppendLine();
            script.AppendLine("-- DAQUI PARA BAIXO SÃO GRANTS DE TABELAS OU SIMILARES DEFINIDOS DIRETO AO USUÁRIO AO INVÉS DE SEREM DEFINIDOS PARA ROLES");
            script.AppendLine();
            script.AppendLine(Separator);

            foreach (var grant in objectGrants.Where(g => g.Column is null))
            {
                AppendStatement(script, $"GRANT {grant.Privilege} ON {FormatObject(grant)} TO {user}{GrantOption(grant.Grantable)}");
            }

            // Privilégios em colunas: um GRANT por privilégio, com todas as colunas.
            var columnGrants = objectGrants
                .Where(g => g.Column is not null)
                .GroupBy(g => (g.Owner, g.ObjectName, g.ObjectType, g.Privilege, g.Grantable));
            foreach (var group in columnGrants)
            {
                var columns = string.Join(", ", group.Select(g => OracleDdlExtensions.QuoteIfNeeded(g.Column!)));
                AppendStatement(script, $"GRANT {group.Key.Privilege} ({columns}) ON {FormatObject(group.First())} TO {user}{GrantOption(group.Key.Grantable)}");
            }
        }

        return script.ToString().ReplaceLineEndings("\r\n");
    }

    private async Task<List<TablespaceQuota>> GetQuotasAsync(string userName, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TABLESPACE_NAME, MAX_BYTES
            FROM DBA_TS_QUOTAS
            WHERE USERNAME = :userName
              AND DROPPED = 'NO'
            ORDER BY TABLESPACE_NAME
            """;

        await using var command = Command(sql, ("userName", userName));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var quotas = new List<TablespaceQuota>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var maxBytes = reader.IsDBNull(1) ? -1 : (long)reader.GetDecimal(1);
            quotas.Add(new TablespaceQuota(reader.GetString(0), maxBytes < 0 ? null : maxBytes));
        }

        return quotas;
    }

    private async Task<List<SystemPrivilegeGrant>> GetSystemPrivilegesAsync(string grantee, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT PRIVILEGE, ADMIN_OPTION
            FROM DBA_SYS_PRIVS
            WHERE GRANTEE = :grantee
            ORDER BY PRIVILEGE
            """;

        await using var command = Command(sql, ("grantee", grantee));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var privileges = new List<SystemPrivilegeGrant>();
        while (await reader.ReadAsync(cancellationToken))
        {
            privileges.Add(new SystemPrivilegeGrant(reader.GetString(0), reader.GetStringOrEmpty(1) == "YES"));
        }

        return privileges;
    }

    private async Task<List<RoleGrant>> GetRolesAsync(string grantee, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT GRANTED_ROLE, ADMIN_OPTION, DEFAULT_ROLE
            FROM DBA_ROLE_PRIVS
            WHERE GRANTEE = :grantee
            ORDER BY GRANTED_ROLE
            """;

        await using var command = Command(sql, ("grantee", grantee));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var roles = new List<RoleGrant>();
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(new RoleGrant(reader.GetString(0), reader.GetStringOrEmpty(1) == "YES", reader.GetStringOrEmpty(2) == "YES"));
        }

        return roles;
    }

    // Grants de objetos (inclusive em colunas) concedidos diretamente ao usuário.
    private async Task<List<ObjectPrivilegeGrant>> GetObjectPrivilegesAsync(string grantee, CancellationToken cancellationToken)
    {
        // Agrupa para não repetir o mesmo privilégio concedido por grantors diferentes.
        const string sql = $"""
            SELECT G.OWNER,
                   G.TABLE_NAME,
                   G.PRIVILEGE,
                   G.GRANTABLE,
                   G.COLUMN_NAME,
                   {ObjectTypeSql} AS OBJECT_TYPE
            FROM (
                SELECT P.OWNER, P.TABLE_NAME, P.PRIVILEGE, MAX(P.GRANTABLE) AS GRANTABLE, CAST(NULL AS VARCHAR2(128)) AS COLUMN_NAME
                FROM DBA_TAB_PRIVS P
                WHERE P.GRANTEE = :grantee
                GROUP BY P.OWNER, P.TABLE_NAME, P.PRIVILEGE
                UNION ALL
                SELECT C.OWNER, C.TABLE_NAME, C.PRIVILEGE, MAX(C.GRANTABLE), C.COLUMN_NAME
                FROM DBA_COL_PRIVS C
                WHERE C.GRANTEE = :grantee
                GROUP BY C.OWNER, C.TABLE_NAME, C.PRIVILEGE, C.COLUMN_NAME
            ) G
            ORDER BY OBJECT_TYPE, G.OWNER, G.TABLE_NAME, G.COLUMN_NAME NULLS FIRST, G.PRIVILEGE
            """;

        await using var command = Command(sql, ("grantee", grantee));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var grants = new List<ObjectPrivilegeGrant>();
        while (await reader.ReadAsync(cancellationToken))
        {
            grants.Add(new ObjectPrivilegeGrant(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetStringOrEmpty(5),
                reader.GetString(2),
                reader.GetStringOrEmpty(3) == "YES",
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return grants;
    }

    // Diretórios não têm owner na sintaxe do GRANT.
    private static string FormatObject(ObjectPrivilegeGrant grant) =>
        grant.IsDirectory
            ? $"DIRECTORY {OracleDdlExtensions.QuoteIfNeeded(grant.ObjectName)}"
            : OracleDdlExtensions.QuoteIfNeeded(grant.Owner, grant.ObjectName);

    // Grants de objetos só aceitam WITH GRANT OPTION.
    private static string GrantOption(bool grantable) => grantable ? " WITH GRANT OPTION" : "";

    private static void AppendClause(StringBuilder script, string clause, string value)
    {
        if (value.Length > 0)
        {
            script.AppendLine($"{clause} {OracleDdlExtensions.QuoteIfNeeded(value)}");
        }
    }

    private static void AppendStatement(StringBuilder script, string statement) =>
        script.AppendLine(statement).AppendLine("/");
}
