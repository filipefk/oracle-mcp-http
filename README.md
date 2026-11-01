# Oracle MCP HTTP

Servidor MCP (Model Context Protocol) em .NET 10, exposto via HTTP, para conectar num banco **Oracle** e executar consultas e comandos SQL/PL-SQL.

Usa o driver **Oracle.ManagedDataAccess.Core** (ODP.NET Managed), 100% gerenciado: **não exige Oracle Client nem Instant Client instalado**, funciona em Windows, Linux e no container Docker.

## Recursos

- **Tools Oracle**: `connect`, `disconnect`, `execute_query`, `execute_command`, `extract_ddl`, `extract_user_script`, `database_info`, `list_objects`, `list_object_owners`, `list_db_links`, `test_db_links`, `list_scheduler_jobs`.
- **Tools de shell** (opcionais): `get_shell_info`, `execute_shell_command`.
- **Controle de API Key**: todas as chamadas a `/mcp` exigem o header `x-api-key`.
- **Tela para gerar a API Key** (`/`): monta a key com a chave MCP e as credenciais do Oracle, testa a conexão e decodifica keys existentes.

## Executar

```bash
dotnet run --project src/Backend/OracleMcpHttp
```

- Tela da API Key: http://localhost:5464/
- Endpoint MCP: http://localhost:5464/mcp
- Health check: http://localhost:5464/health

## API Key

O header `x-api-key` é uma única string que junta (embaralhadas, não criptografadas):

1. a **chave MCP** — precisa estar em `McpAuth:ApiKeys` no servidor;
2. opcionalmente a **connection string do Oracle**.

Gere a key pela tela da raiz do servidor (ou pelo endpoint `POST /api-key/generate`). Na tela é possível preencher host, porta, Service Name/SID, usuário, senha e privilégio (SYSDBA/SYSOPER), ou colar uma connection string pronta, e **testar a conexão** antes de gerar.

Trate a key como uma senha: quem a tiver consegue recuperar o usuário e a senha do Oracle.

| Endpoint | Header `x-api-key` | Corpo |
|---|---|---|
| `POST /api-key/generate` | chave MCP em texto puro | `{ "mcpApiKey": "...", "connectionString": "..." }` |
| `POST /api-key/decode` | chave MCP em texto puro | `{ "apiKey": "..." }` |
| `POST /api-key/test-connection` | chave MCP em texto puro | `{ "connectionString": "..." }` |
| `/mcp` | key gerada | JSON-RPC do MCP |

Adicionar no Claude Code:

```bash
claude mcp add --transport http oracle http://localhost:5464/mcp --header "x-api-key: <key gerada>"
```

## Connection string

Formato do ODP.NET:

```
User Id=APP_USER;Password=senha;Data Source=//host:1521/ORCLPDB1
```

O `Data Source` aceita EZConnect (`//host:porta/servico`) ou o descritor completo (`(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=host)(PORT=1521))(CONNECT_DATA=(SID=ORCL)))`). Para conectar como SYSDBA, acrescente `DBA Privilege=SYSDBA`.

A tool `connect` escolhe a connection string nesta ordem:

1. a gravada na API Key;
2. a do servidor (`Oracle:ConnectionString` ou variável de ambiente `ORACLE_CONNECTION`);
3. o parâmetro `connection_string` da tool.

## Tools Oracle

| Tool | Descrição |
|---|---|
| `connect(connection_string?)` | Abre a conexão da sessão MCP e retorna versão, banco, serviço, usuário e schema. |
| `disconnect()` | Fecha a conexão da sessão. Idempotente. |
| `execute_query(sql, parameters_json?, max_rows = 1000)` | Executa um SELECT e retorna colunas (nome/tipo) e linhas em JSON. |
| `execute_command(sql, parameters_json?)` | Executa INSERT/UPDATE/DELETE/MERGE, DDL ou PL/SQL e retorna as linhas afetadas. |
| `extract_ddl(object_name, include_header = false, inline_compile_errors = false)` | Retorna a DDL completa e pronta de um objeto (tabela, view, sequence, procedure, function, package, type, trigger, java source, job), com sinônimos e grants. Para tabela inclui comentários, constraints, índices e triggers. Opções de cabeçalho e de mensagem de erro de compilação na linha. |
| `extract_user_script(user_names)` | Gera o script de criação de um ou mais usuários (CREATE USER com `<SENHA_USUARIO>`, tablespaces, profile, cotas, privilégios de sistema, roles e grants diretos de objetos/colunas). Exige leitura das views `DBA_*`. |
| `database_info()` | Retorna informações do banco agrupadas (banco de dados, instância, armazenamento, NLS, sessão, cliente). O que depende de views sem privilégio (`V$*`, `DBA_*`) é omitido. |
| `list_objects(mode = "search", owner?, object_type?, name?, days = 15, max_rows = 1000)` | Lista objetos como a tela "Extract DDL Oracle": `search` (FUNCTION, JOB, PACKAGE, PROCEDURE, SEQUENCE, TABLE, TRIGGER, TYPE, VIEW, JAVA SOURCE por owner/tipo/parte do nome), `invalid` (inválidos de qualquer tipo, com os erros de compilação) ou `recent` (compilados/alterados nos últimos N dias, incluindo tabelas). Retorna owner, nome, tipo, criação, alteração, status e mensagem de erro. |
| `list_object_owners()` | Lista os owners com objetos em `ALL_OBJECTS` (filtro de `list_objects`). |
| `list_db_links()` | Lista os DBLINKs de `ALL_DB_LINKS` (criação, owner, nome, usuário, host), como a tela "DBLINKs". |
| `test_db_links(db_links?)` | Testa os DBLINKs informados (ou todos) com `SELECT DUMMY FROM DUAL@link` (timeout de 60s) e fecha cada link após o teste. Retorna `TESTE OK` ou `ERRO: <mensagem>` por link. |
| `list_scheduler_jobs()` | Lista os jobs de `ALL_SCHEDULER_JOBS` com as mesmas colunas da tela "Jobs de banco" e a situação (OK, desabilitado, atrasado, rodando há mais de 1 hora), com resumo por situação. |

- **Uma conexão por sessão MCP**: cada cliente usa as próprias credenciais, sem misturar usuários. A conexão é fechada após `Oracle:IdleTimeoutMinutes` de inatividade.
- **Bind variables**: `parameters_json` = `{"id": 42}` e no SQL `:id`. Booleanos vão como 1/0; datas como texto convertido no SQL (`TO_DATE(:dt, 'YYYY-MM-DD')`).
- **Terminadores**: o `;` final é removido de SQL simples (evita ORA-00911) e mantido em PL/SQL; uma `/` final (estilo SQL*Plus) também é removida.
- **Autocommit**: cada comando é confirmado automaticamente.
- **Tipos**: `NUMBER` que não cabe em `decimal` volta como texto; `BLOB`/`RAW` voltam em base64; colunas com nome repetido recebem sufixo (`ID_2`).

## Tools de shell

Desligadas por padrão. Com `"Shell:Enabled": true` o servidor expõe:

- `get_shell_info` — sistema operacional, shell padrão, limites e se exige senha.
- `execute_shell_command(command, shell?, working_directory?, timeout_seconds?, password?)` — executa um comando **no servidor** (PowerShell/cmd no Windows, bash/sh no Linux).

Opções em `Shell`: `DefaultShell`, `WorkingDirectory`, `TimeoutSeconds`, `MaxOutputChars`, `Password`, `AllowedCommands`, `BlockedCommands`.

## Configuração (`appsettings.json`)

```json
{
  "McpAuth": { "ApiKeys": [ "minha-chave-mcp" ] },
  "RateLimit": { "McpPermitsPerMinute": 300, "ApiKeyPermitsPerMinute": 10 },
  "Oracle": { "ConnectionString": "", "IdleTimeoutMinutes": 10, "CommandTimeoutSeconds": 0 },
  "Shell": { "Enabled": false, "Password": "" }
}
```

- `McpAuth:ApiKeys` vazio rejeita todas as chamadas (401).
- `Oracle:CommandTimeoutSeconds` = 0 significa sem limite por comando.
- Os logs (`logs/log*.txt`) mascaram o header `x-api-key` e senhas de connection strings.

## Docker

```bash
docker build -t oracle-mcp .
```

```bash
docker run -p 8080:8080 -e McpAuth__ApiKeys__0=minha-chave-mcp -e ORACLE_CONNECTION="User Id=...;Password=...;Data Source=//host:1521/SERVICO" oracle-mcp
```

## Testes

```bash
dotnet test
```

A collection do Postman (`OracleMcpHttp.postman_collection.json` + environment) cobre geração da key, handshake MCP e todas as tools.
