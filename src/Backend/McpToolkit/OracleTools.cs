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

    [McpServerTool(Title = "Extrair DDL de objeto", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Extrai a DDL completa e pronta para executar de um objeto do banco, usando a conexão Oracle ativa da sessão (chame 'connect' antes). Tipos suportados: TABLE, VIEW, SEQUENCE, PROCEDURE, FUNCTION, PACKAGE (spec + body), TYPE (spec + body), TRIGGER, JAVA SOURCE e JOB (DBMS_SCHEDULER). Junto do objeto vêm os sinônimos que apontam para ele e os grants concedidos. Para TABLE a DDL inclui CREATE TABLE (colunas, DEFAULT, NOT NULL, colunas virtuais e identity), comentários, constraints (PK, UK, FK e CHECK), índices (exceto os que sustentam PK/UK) e as triggers da tabela; partições, storage e tablespace não são gerados. Cada instrução termina com '/' em linha própria (estilo SQL*Plus); para rodar via execute_command, envie uma instrução por vez. Retorna o nome qualificado (OWNER.NOME), o tipo do objeto e o texto da DDL.")]
    public async Task<string> extract_ddl(
        [Description("Nome do objeto, no formato OWNER.NOME ou só NOME (nesse caso usa o schema corrente da sessão). Não diferencia maiúsculas/minúsculas.")] string object_name,
        [Description("Se true, inclui um cabeçalho em comentário com dados da conexão, data de criação, última compilação, status do objeto e mensagens de erro de compilação (padrão false).")] bool include_header = false,
        [Description("Se true, acrescenta ao fim de cada linha com erro de compilação o comentário '-- ###ERRO### <mensagem>' (vale para PL/SQL, inclusive triggers de tabela; padrão false).")] bool inline_compile_errors = false,
        CancellationToken ct = default)
    {
        var result = await oracle.ExtractDdlAsync(object_name, include_header, inline_compile_errors, ct);
        return result.ToJsonString();
    }

    [McpServerTool(Title = "Extrair script de usuário", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Gera o script de criação de um ou mais usuários do banco, usando a conexão Oracle ativa da sessão (chame 'connect' antes): CREATE USER com a senha como marcador <SENHA_USUARIO> (troque antes de executar), tablespaces default e temporária, profile, cotas, ACCOUNT LOCK/PASSWORD EXPIRE, privilégios de sistema, roles (com ajuste das roles default) e grants de objetos e colunas concedidos diretamente ao usuário. Cada instrução termina com '/' em linha própria. Lê views DBA_* (DBA_USERS, DBA_TS_QUOTAS, DBA_SYS_PRIVS, DBA_ROLE_PRIVS, DBA_TAB_PRIVS, DBA_COL_PRIVS, DBA_OBJECTS), então exige privilégio de leitura do dicionário (ex.: SELECT_CATALOG_ROLE ou SELECT ANY DICTIONARY). Retorna os usuários gerados, os erros (usuário não encontrado, sem privilégio) e o script combinado.")]
    public async Task<string> extract_user_script(
        [Description("Nome do usuário, ou vários separados por vírgula, ponto e vírgula ou quebra de linha. Não diferencia maiúsculas/minúsculas.")] string user_names,
        CancellationToken ct = default)
    {
        var result = await oracle.ExtractUserScriptAsync(user_names, ct);
        return result.ToJsonString();
    }

    [McpServerTool(Title = "Listar objetos do banco", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lista objetos do banco (visíveis em ALL_OBJECTS) usando a conexão Oracle ativa da sessão (chame 'connect' antes), como a tela 'Extract DDL Oracle' do OracleTools2026. PACKAGE BODY e TYPE BODY são agrupados em PACKAGE e TYPE; o status é INVALID se qualquer parte estiver inválida. Modos: 'search' (padrão) lista os tipos FUNCTION, JOB, PACKAGE, PROCEDURE, SEQUENCE, TABLE, TRIGGER, TYPE, VIEW e JAVA SOURCE filtrando por owner, tipo e parte do nome; 'invalid' lista objetos inválidos de qualquer tipo, com os mesmos filtros; 'recent' lista os objetos compilados/alterados (LAST_DDL_TIME) ou jobs criados nos últimos N dias (parâmetro days), ignorando os demais filtros; inclui tabelas (o LAST_DDL_TIME de tabela muda também com GRANT/REVOKE e alguns comandos como TRUNCATE), mas não partições, índices, sinônimos, LOBs e types. Para objetos inválidos, 'errorMessage' traz os erros de compilação (linha, coluna, mensagem e texto da linha). Retorna owner, nome, tipo, nome qualificado (OWNER.NOME, aceito por extract_ddl), data de criação, data de alteração e status.")]
    public async Task<string> list_objects(
        [Description("Modo da listagem: 'search' (padrão), 'invalid' ou 'recent'.")] string mode = "search",
        [Description("Owner (schema) exato, ex: HR. Vazio ou 'Todos' = todos. Use list_object_owners para ver os disponíveis. Ignorado no modo 'recent'.")] string? owner = null,
        [Description("Tipo do objeto: FUNCTION, JOB, PACKAGE, PROCEDURE, SEQUENCE, TABLE, TRIGGER, TYPE, VIEW ou JAVA SOURCE. Vazio ou 'Todos' = todos. Ignorado no modo 'recent'.")] string? object_type = null,
        [Description("Parte do nome do objeto (contém, sem diferenciar maiúsculas/minúsculas). Ignorado no modo 'recent'.")] string? name = null,
        [Description("Quantidade de dias para o modo 'recent' (padrão 15, máximo 3650).")] int days = 15,
        [Description("Quantidade máxima de objetos retornados (padrão 1000); verifique 'truncated' e 'totalCount' na resposta.")] int max_rows = 1000,
        CancellationToken ct = default)
    {
        var result = await oracle.ListObjectsAsync(mode, owner, object_type, name, days, max_rows, ct);
        return result.ToJsonString();
    }

    [McpServerTool(Title = "Listar owners de objetos", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lista os owners (schemas) distintos que têm objetos visíveis em ALL_OBJECTS para a conexão Oracle ativa da sessão (chame 'connect' antes). Útil para escolher o filtro 'owner' de list_objects.")]
    public async Task<string> list_object_owners(CancellationToken ct = default)
    {
        var result = await oracle.ListObjectOwnersAsync(ct);
        return result.ToJsonString();
    }

    [McpServerTool(Title = "Listar DBLINKs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lista os database links visíveis (ALL_DB_LINKS) para a conexão Oracle ativa da sessão (chame 'connect' antes), como a tela 'DBLINKs' do OracleTools2026: data de criação, owner, nome do DBLINK, usuário remoto e host (TNS/descritor). Para testar a conectividade use test_db_links.")]
    public async Task<string> list_db_links(CancellationToken ct = default)
    {
        var result = await oracle.ListDatabaseLinksAsync(ct);
        return result.ToJsonString();
    }

    [McpServerTool(Title = "Testar DBLINKs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true), Description("Testa database links da conexão Oracle ativa da sessão (chame 'connect' antes), como a tela 'DBLINKs' do OracleTools2026: executa SELECT DUMMY FROM DUAL@<dblink> (timeout de 60s por link) e fecha o link em seguida (ROLLBACK + ALTER SESSION CLOSE DATABASE LINK, para não estourar o limite de links abertos por sessão). Atenção: o ROLLBACK descarta qualquer transação pendente da sessão. Sem o parâmetro db_links testa todos os listados em ALL_DB_LINKS (pode demorar). Links privados de outros usuários normalmente falham, pois só o dono pode usá-los. Retorna, por link, os dados da listagem mais 'ok' e 'test' ('TESTE OK' ou 'ERRO: <mensagem>'), além dos totais e dos nomes não encontrados.")]
    public async Task<string> test_db_links(
        [Description("Nomes dos DBLINKs a testar (NOME ou OWNER.NOME), separados por vírgula, ponto e vírgula ou quebra de linha. Vazio = testa todos.")] string? db_links = null,
        CancellationToken ct = default)
    {
        var result = await oracle.TestDatabaseLinksAsync(db_links, ct);
        return result.ToJsonString();
    }

    [McpServerTool(Title = "Listar jobs de banco", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lista os jobs do DBMS_SCHEDULER visíveis (ALL_SCHEDULER_JOBS) para a conexão Oracle ativa da sessão (chame 'connect' antes), como a tela 'Jobs de banco' do OracleTools2026, ordenados por habilitado e última execução (mais recente primeiro). Por job: criação e última alteração do objeto, owner, nome, ação, enabled, tipo, programa, schedule (owner, nome, tipo, repeat interval), início da última execução, duração da última execução, próxima execução (datas no fuso da sessão), quantidade de execuções e falhas, comentários, state e 'health': 'OK', 'Desabilitado', 'Atrasado (possivelmente travado)' (próxima execução vencida há mais de 1 minuto) ou 'Rodando há mais de 1 hora'. Jobs de execução única (schedule ONCE/NAMED/IMMEDIATE) nunca são considerados atrasados. Inclui um resumo com a contagem por situação. Para a DDL de um job use extract_ddl com OWNER.NOME_DO_JOB.")]
    public async Task<string> list_scheduler_jobs(CancellationToken ct = default)
    {
        var result = await oracle.ListSchedulerJobsAsync(ct);
        return result.ToJsonString();
    }

    [McpServerTool(Title = "Informações do banco de dados", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Retorna informações do banco Oracle da conexão ativa da sessão (chame 'connect' antes), agrupadas em: 'Banco de dados' (versão, produto, nome, serviço, instância, servidor, fuso horário, data/hora, container PDB, criação, modo de log/abertura, papel, plataforma), 'Instância' (host, status, início, tempo no ar), 'Armazenamento' (GB alocado em datafiles e usado em segmentos), 'NLS' (character sets, idioma, território), 'Sessão' (usuário, schema atual, SID, usuário do SO, terminal, IP, fuso, banco, privilégio SYSDBA) e 'Cliente' (versões do driver, do servidor MCP e do .NET). Informações que dependem de views sem privilégio (V$*, DBA_*) ou inexistentes na versão do banco são omitidas, sem erro.")]
    public async Task<string> database_info(CancellationToken ct = default)
    {
        var result = await oracle.GetDatabaseInfoAsync(ct);
        return result.ToJsonString();
    }
}
