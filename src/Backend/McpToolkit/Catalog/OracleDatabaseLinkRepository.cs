using McpToolkit.Ddl;
using Oracle.ManagedDataAccess.Client;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace McpToolkit.Catalog;

// Portado de OracleTools2026.Core\Data\DatabaseLinkRepository (tela "DBLINKs").
internal sealed partial class OracleDatabaseLinkRepository(OracleConnection connection, int commandTimeoutSeconds)
{
    private const int TestTimeoutSeconds = 60;

    public async Task<List<DatabaseLink>> GetLinksAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CREATED,
                   OWNER,
                   DB_LINK,
                   USERNAME,
                   HOST
            FROM ALL_DB_LINKS
            ORDER BY OWNER, DB_LINK
            """;

        await using var command = connection.CreateTextCommand(sql);
        if (commandTimeoutSeconds > 0)
        {
            command.CommandTimeout = commandTimeoutSeconds;
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var links = new List<DatabaseLink>();
        while (await reader.ReadAsync(cancellationToken))
        {
            links.Add(new DatabaseLink(
                reader.GetDateTimeOrNull(0),
                reader.GetStringOrEmpty(1),
                reader.GetStringOrEmpty(2),
                reader.GetStringOrEmpty(3),
                reader.GetStringOrEmpty(4)));
        }

        return links;
    }

    // Devolve null quando o teste passou, senão a mensagem de erro.
    public async Task<string?> TestLinkAsync(string linkName, CancellationToken cancellationToken)
    {
        // O nome do DBLINK não pode ser bind variable; só nomes com caracteres válidos sem aspas são concatenados.
        if (!LinkNamePattern().IsMatch(linkName))
        {
            return "Nome de DBLINK não suportado para teste.";
        }

        try
        {
            await using var command = connection.CreateTextCommand($"SELECT DUMMY FROM DUAL@{linkName}");
            command.CommandTimeout = TestTimeoutSeconds;
            await using var cancelRegistration = cancellationToken.Register(command.Cancel);
            await command.ExecuteScalarAsync(cancellationToken);
            return null;
        }
        catch (OracleException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OracleException ex)
        {
            return ex.Message;
        }
        finally
        {
            await CloseLinkAsync(linkName);
        }
    }

    // Fecha o link depois de cada teste para não estourar o limite de links abertos por sessão
    // (ORA-02020). O ROLLBACK encerra a transação distribuída aberta pela consulta; sem ele o
    // link não pode ser fechado (ORA-02080).
    private async Task CloseLinkAsync(string linkName)
    {
        foreach (var sql in new[] { "ROLLBACK", $"ALTER SESSION CLOSE DATABASE LINK {linkName}" })
        {
            try
            {
                await using var command = connection.CreateTextCommand(sql);
                await command.ExecuteNonQueryAsync();
            }
            catch (OracleException)
            {
                // O link pode já estar fechado ou nem ter sido aberto.
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_$#.@]*$")]
    private static partial Regex LinkNamePattern();
}

internal sealed record DatabaseLink(DateTime? CreatedAt, string Owner, string Name, string UserName, string Host)
{
    public JsonObject ToJson() => new()
    {
        ["created"] = CatalogFormat.Date(CreatedAt),
        ["owner"] = Owner,
        ["dbLink"] = Name,
        ["username"] = UserName,
        ["host"] = Host
    };
}
