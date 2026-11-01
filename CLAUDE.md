# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

OracleMcpHttp is an MCP (Model Context Protocol) server exposed over HTTP that lets MCP clients connect to an Oracle database and run SQL/PL-SQL through a small set of tools. It targets .NET 10 and uses `Oracle.ManagedDataAccess.Core` (fully managed ODP.NET — no Oracle Client install needed). It mirrors `../postgre-mcp-http` (database tools) and `../n8n-mcp-http` (shell tools, API key auth, API key page).

## Commands

```bash
dotnet build                                   # solution file: OracleMcpHttp.slnx
dotnet run --project src/Backend/OracleMcpHttp # http://localhost:5464 (page at /, MCP at /mcp)
dotnet test
dotnet test --filter "FullyQualifiedName~ClassName.MethodName"
```

`Apagar-todas-pastas-bin-obj.cmd` recursively deletes all `bin`/`obj` folders (manual cleanup helper).

## Architecture

- **GeraApiKey** — `ApiKeyGenerator` packs a list of strings into one header-safe key (XOR scramble + Base64Url; obfuscation, not encryption). Copied verbatim from n8n-mcp-http.
- **McpToolkit** — tool logic. No ASP.NET Core dependency; HTTP state comes through `IRequestContext`.
  - `OracleConnectionManager` (singleton): **one OracleConnection per MCP session** (keyed by the `Mcp-Session-Id` header, `"default"` when absent), each guarded by its own `SemaphoreSlim`. Sessions removed by disconnect/idle are flagged `Removed` so a concurrent `connect` retries with a fresh session instead of attaching to an orphan.
  - `OracleIdleTimeoutService`: every 30s closes sessions idle longer than `Oracle:IdleTimeoutMinutes` (skips sessions whose lock is busy).
  - `OracleSql`: SQL normalization (strip trailing `;` and SQL*Plus `/` except for PL/SQL), JSON ⇄ CLR conversion, bind parameters (`BindByName = true`, `:name`), unique column names.
  - `OracleTools`: `connect`, `disconnect`, `execute_query`, `execute_command`, `extract_ddl`, `extract_user_script`, `database_info`, `list_objects`, `list_object_owners`, `list_db_links`, `test_db_links`, `list_scheduler_jobs`.
  - `Catalog/*`: ported from the OracleTools2026 repositories behind the "Extract DDL Oracle" (`DatabaseObjectRepository` search/recent/invalid + compile errors), "DBLINKs" (`DatabaseLinkRepository`) and "Jobs de banco" (`SchedulerJobRepository`) screens; same SQL and job health rules (except `list_objects` also includes TABLE in the search and recent modes), dates formatted `dd/MM/yyyy HH:mm:ss`.
  - `Ddl/OracleUserScriptGenerator`: ported from `UserScriptGenerator` + the `DatabaseUserRepository` queries it uses (OracleTools2026); reads `DBA_*` views.
  - `OracleDatabaseInfo`: ported from `DatabaseObjectRepository.GetDatabaseDetailsAsync` (OracleTools2026); each query is independent and silently skipped on `OracleException` (missing privilege/older version).
  - `Ddl/OracleDdlExtractor`: ported from `OracleTools2026.Core\Data\DdlExtractor.cs` (same output, header and inline compile-error options) plus TABLE support built from `ALL_*` views (columns, comments, constraints, indexes, triggers). Runs on the session connection via `OracleConnectionManager.ExtractDdlAsync`.
  - `Shell/*`: shell tools, copied verbatim from n8n-mcp-http.
- **OracleMcpHttp** — ASP.NET Core host.
  - `McpApiKey`: the x-api-key value order is `[mcpApiKey, oracleConnectionString]`; only `ToValues`/`FromValues` know it.
  - `McpApiKeyMiddleware`: `/mcp` reads the encoded key, `/api-key/*` reads the raw MCP key; validates against `McpAuth:ApiKeys` in constant time; empty list rejects everything.
  - `McpTrafficLoggingMiddleware` (only `/mcp`): masks `x-api-key`, shell `password` and `Password=`/`Pwd=` in connection strings.
  - `Endpoints/ApiKeyEndpoints`: `generate`, `decode`, `test-connection`.
  - `wwwroot/index.html`: API key page (fields or raw connection string, connection test, decode).
  - Global per-IP rate limiter (`RateLimit:*`) runs before auth.

## Connection string precedence

`connect` uses: API key value → `Oracle:ConnectionString` / env `ORACLE_CONNECTION` → `connection_string` tool parameter. The response field `source` says which one was used. Connection strings are never returned or persisted.

## Conventions

- Tool I/O uses `JsonObject`/`JsonNode`, not DTOs. Tool/user-facing text is in Portuguese (pt-BR).
- `Shell:Enabled` defaults to false.
- `appsettings.Development.json` holds a dev MCP key; keep real credentials out of version control.
