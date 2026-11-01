# CLAUDE.md

Este arquivo orienta o Claude Code (claude.ai/code) ao trabalhar com o código deste repositório.

## Visão geral

OracleMcpHttp é um servidor MCP (Model Context Protocol) exposto via HTTP que permite a clientes MCP conectar a um banco Oracle e executar SQL/PL-SQL por meio de um pequeno conjunto de tools. Usa .NET 10 e `Oracle.ManagedDataAccess.Core` (ODP.NET 100% gerenciado — não precisa instalar o Oracle Client). Segue o mesmo modelo de `../postgre-mcp-http` (tools de banco) e `../n8n-mcp-http` (tools de shell, autenticação por API key, tela da API key).

## Comandos

```bash
dotnet build                                   # solution: OracleMcpHttp.slnx
dotnet run --project src/Backend/OracleMcpHttp # http://localhost:5464 (tela em /, MCP em /mcp)
dotnet test
dotnet test --filter "FullyQualifiedName~ClassName.MethodName"
```

`Apagar-todas-pastas-bin-obj.cmd` apaga recursivamente todas as pastas `bin`/`obj` (auxiliar de limpeza manual).

## Arquitetura

- **GeraApiKey** — `ApiKeyGenerator` empacota uma lista de strings em uma única key segura para header (embaralhamento XOR + Base64Url; ofuscação, não criptografia). Copiado sem alterações do n8n-mcp-http.
- **McpToolkit** — lógica das tools. Sem dependência de ASP.NET Core; o estado HTTP chega via `IRequestContext`.
  - `OracleConnectionManager` (singleton): **uma OracleConnection por sessão MCP** (chave = header `Mcp-Session-Id`, `"default"` quando ausente), cada uma protegida por seu próprio `SemaphoreSlim`. Sessões removidas por disconnect/inatividade são marcadas como `Removed`, para que um `connect` concorrente tente de novo com uma sessão nova em vez de se anexar a uma órfã.
  - `OracleIdleTimeoutService`: a cada 30s fecha as sessões inativas há mais de `Oracle:IdleTimeoutMinutes` (ignora sessões cujo lock está ocupado).
  - `OracleSql`: normalização de SQL (remove `;` final e `/` do SQL*Plus, exceto em PL/SQL), conversão JSON ⇄ CLR, bind parameters (`BindByName = true`, `:nome`), nomes de coluna únicos.
  - `OracleTools`: `connect`, `disconnect`, `execute_query`, `execute_command`, `extract_ddl`, `extract_user_script`, `database_info`, `list_objects`, `list_object_owners`, `list_db_links`, `test_db_links`, `list_scheduler_jobs`.
  - `Catalog/*`: portado dos repositórios do OracleTools2026 usados pelas telas "Extract DDL Oracle" (`DatabaseObjectRepository` busca/recentes/inválidos + erros de compilação), "DBLINKs" (`DatabaseLinkRepository`) e "Jobs de banco" (`SchedulerJobRepository`); mesmo SQL e mesmas regras de saúde dos jobs (exceto que `list_objects` também inclui TABLE nos modos search e recent), datas no formato `dd/MM/yyyy HH:mm:ss`.
  - `Ddl/OracleUserScriptGenerator`: portado de `UserScriptGenerator` + as consultas do `DatabaseUserRepository` que ele usa (OracleTools2026); lê as views `DBA_*`.
  - `OracleDatabaseInfo`: portado de `DatabaseObjectRepository.GetDatabaseDetailsAsync` (OracleTools2026); cada consulta é independente e ignorada silenciosamente em caso de `OracleException` (falta de privilégio/versão antiga).
  - `Ddl/OracleDdlExtractor`: portado de `OracleTools2026.Core\Data\DdlExtractor.cs` (mesma saída, mesmas opções de cabeçalho e de erro de compilação na linha) mais suporte a TABLE montado a partir das views `ALL_*` (colunas, comentários, constraints, índices, triggers). Roda na conexão da sessão via `OracleConnectionManager.ExtractDdlAsync`.
  - `Shell/*`: tools de shell, copiadas sem alterações do n8n-mcp-http.
- **OracleMcpHttp** — host ASP.NET Core.
  - `McpApiKey`: a ordem dos valores no x-api-key é `[mcpApiKey, oracleConnectionString]`; só `ToValues`/`FromValues` conhecem essa ordem.
  - `McpApiKeyMiddleware`: `/mcp` lê a key codificada, `/api-key/*` lê a chave MCP pura; valida contra `McpAuth:ApiKeys` em tempo constante; lista vazia rejeita tudo.
  - `McpTrafficLoggingMiddleware` (apenas `/mcp`): mascara `x-api-key`, o `password` do shell e `Password=`/`Pwd=` nas connection strings.
  - `Endpoints/ApiKeyEndpoints`: `generate`, `decode`, `test-connection`.
  - `wwwroot/index.html`: tela da API key (campos ou connection string pura, teste de conexão, decodificação).
  - Rate limiter global por IP (`RateLimit:*`) roda antes da autenticação.

## Precedência da connection string

`connect` usa: valor da API key → `Oracle:ConnectionString` / variável de ambiente `ORACLE_CONNECTION` → parâmetro `connection_string` da tool. O campo `source` da resposta informa qual foi usada. Connection strings nunca são retornadas nem persistidas.

## Convenções

- Entrada/saída das tools usa `JsonObject`/`JsonNode`, não DTOs. Textos das tools e voltados ao usuário ficam em português (pt-BR).
- `Shell:Enabled` é false por padrão.
- `appsettings.Development.json` contém uma chave MCP de desenvolvimento; mantenha credenciais reais fora do controle de versão.
