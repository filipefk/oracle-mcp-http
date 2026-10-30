using ModelContextProtocol.Server;
using System.ComponentModel;

namespace McpToolkit;

[McpServerToolType]
public sealed class OracleTools(OracleConnectionManager oracle)
{
    [McpServerTool(Title = "Conectar ao Oracle", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Abre uma conexão com um banco Oracle e a mantém aberta em memória para esta sessão MCP, para uso pelas demais ferramentas (execute_query/execute_command), até ser fechada explicitamente (disconnect) ou até o timeout de inatividade configurado no servidor. Substitui qualquer conexão anterior da sessão. A connection string usada segue esta ordem: a gravada na API Key (header x-api-key), a configurada no servidor (Oracle:ConnectionString) e, por último, o parâmetro connection_string. O campo 'source' da resposta indica qual foi usada. A connection string nunca é retornada nem persistida em disco.")]
    public async Task<string> connect(
        [Description("Connection string do Oracle (ODP.NET), ex: User Id=usuario;Password=senha;Data Source=//host:1521/SERVICO. Só é usada se a API Key e o servidor não trouxerem uma.")] string? connection_string = null,
        CancellationToken ct = default)
    {
        var result = await oracle.ConnectAsync(connection_string, ct);
        return result.ToJsonString();
    }

    [McpServerTool(Title = "Desconectar do Oracle", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Fecha a conexão Oracle ativa desta sessão MCP, se houver. Não falha caso já não haja conexão ativa.")]
    public async Task<string> disconnect(CancellationToken ct = default)
    {
        var result = await oracle.DisconnectAsync(ct);
        return result.ToJsonString();
    }

    [McpServerTool(Title = "Executar consulta SQL", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Executa uma consulta SQL (SELECT ou WITH) na conexão Oracle ativa da sessão (chame 'connect' antes). Uma instrução por chamada; o ';' final é removido automaticamente. Suporta bind variables nomeadas via parameters_json (ex: {\"id\": 42}, referenciadas no sql como :id) para evitar concatenar valores no SQL; datas devem ir como texto e ser convertidas no SQL (ex: TO_DATE(:dt, 'YYYY-MM-DD')). Retorna as colunas (nome e tipo) e as linhas como JSON; NUMBER que não cabe em decimal volta como texto, BLOB/RAW voltam em base64. Limita a quantidade de linhas retornadas por max_rows (verifique o campo 'truncated' na resposta). Para limitar no banco use FETCH FIRST n ROWS ONLY (12c+) ou ROWNUM.")]
    public async Task<string> execute_query(
        [Description("Instrução SQL a executar (ex: SELECT * FROM clientes WHERE id = :id)")] string sql,
        [Description("Objeto JSON com as bind variables nomeadas usadas no sql, ex: {\"id\": 42} (opcional)")] string? parameters_json = null,
        [Description("Quantidade máxima de linhas retornadas (padrão 1000)")] int max_rows = 1000,
        CancellationToken ct = default)
    {
        var parameters = OracleSql.ParseParameters(parameters_json);
        var result = await oracle.ExecuteQueryAsync(sql, parameters, max_rows, ct);
        return result.ToJsonString();
    }

    [McpServerTool(Title = "Executar comando SQL", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Executa um comando que não retorna linhas na conexão Oracle ativa da sessão (chame 'connect' antes): INSERT, UPDATE, DELETE, MERGE, DDL (CREATE/ALTER/DROP), blocos PL/SQL anônimos (BEGIN ... END;) e criação de procedures/functions/packages/triggers. Uma instrução por chamada; em SQL simples o ';' final é removido automaticamente, em PL/SQL é mantido. As alterações são confirmadas automaticamente (autocommit). Suporta bind variables nomeadas via parameters_json (ex: {\"nome\": \"João\"}, referenciadas no sql como :nome); booleanos são enviados como 1/0. Retorna a quantidade de linhas afetadas (-1 para DDL e PL/SQL).")]
    public async Task<string> execute_command(
        [Description("Instrução SQL ou bloco PL/SQL a executar (ex: UPDATE clientes SET nome = :nome WHERE id = :id)")] string sql,
        [Description("Objeto JSON com as bind variables nomeadas usadas no sql, ex: {\"id\": 42, \"nome\": \"João\"} (opcional)")] string? parameters_json = null,
        CancellationToken ct = default)
    {
        var parameters = OracleSql.ParseParameters(parameters_json);
        var result = await oracle.ExecuteCommandAsync(sql, parameters, ct);
        return result.ToJsonString();
    }
}
