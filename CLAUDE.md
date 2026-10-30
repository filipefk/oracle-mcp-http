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
  - `OracleTools`: `connect`, `disconnect`, `execute_query`, `execute_command`.
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
