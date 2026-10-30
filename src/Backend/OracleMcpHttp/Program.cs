using McpToolkit;
using McpToolkit.Shell;
using OracleMcpHttp;
using OracleMcpHttp.Endpoints;
using OracleMcpHttp.Middleware;
using Serilog;
using Serilog.Events;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

var strLogLevel = configuration.GetValue<string>("Logging:LogLevel:Default");
var enumLogEventLevel = Enum.TryParse<LogEventLevel>(strLogLevel, true, out var parsedLogEventLevel)
    ? parsedLogEventLevel
    : LogEventLevel.Information;
builder.Host.UseSerilog((context, loggerConfiguration) =>
    loggerConfiguration
        .MinimumLevel.Is(enumLogEventLevel)
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .WriteTo.Console(enumLogEventLevel)
        .WriteTo.File("logs/log.txt",
            enumLogEventLevel,
            rollingInterval: RollingInterval.Day));

builder.Services.Configure<McpOptions>(configuration.GetSection("Oracle"));
// Sem Oracle:ConnectionString configurada, aceita a variável de ambiente ORACLE_CONNECTION —
// permite subir o container só com variáveis, sem montar um appsettings.
builder.Services.PostConfigure<McpOptions>(options =>
{
    if (string.IsNullOrWhiteSpace(options.ConnectionString))
        options.ConnectionString = configuration["ORACLE_CONNECTION"] ?? string.Empty;
});
// Chaves MCP autorizadas (primeiro valor do header x-api-key) — lista vazia rejeita
// todas as chamadas a /mcp e /api-key.
builder.Services.Configure<McpAuthOptions>(configuration.GetSection("McpAuth"));
builder.Services.Configure<ShellOptions>(configuration.GetSection("Shell"));
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IRequestContext, HttpRequestContext>();

// Limite por IP para não deixar a chave MCP exposta a força bruta. Tem de ser o GlobalLimiter, e não
// RequireRateLimiting no endpoint: os UseWhen de autenticação rodam antes do roteamento, então uma
// política por endpoint só seria avaliada depois do 401.
var mcpPermits = configuration.GetValue("RateLimit:McpPermitsPerMinute", 300);
var apiKeyPermits = configuration.GetValue("RateLimit:ApiKeyPermitsPerMinute", 10);
builder.Services.AddRateLimiter(rateLimiter =>
{
    rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateLimiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";

        if (context.Request.Path.StartsWithSegments("/api-key"))
            return RateLimitPartition.GetFixedWindowLimiter($"api-key:{ip}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = apiKeyPermits,
                Window = TimeSpan.FromMinutes(1)
            });

        if (context.Request.Path.StartsWithSegments("/mcp"))
            return RateLimitPartition.GetFixedWindowLimiter($"mcp:{ip}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = mcpPermits,
                Window = TimeSpan.FromMinutes(1)
            });

        return RateLimitPartition.GetNoLimiter("sem-limite");
    });
});

builder.Services.AddSingleton<OracleConnectionManager>();
builder.Services.AddHostedService<OracleIdleTimeoutService>();
builder.Services.AddSingleton<ShellCommandRunner>();

var mcpBuilder = builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<OracleTools>();

// Desligado por padrão: defina "Shell:Enabled": true para expor as tools de execução de comandos.
if (configuration.GetValue("Shell:Enabled", false))
    mcpBuilder.WithTools<ShellTools>();

var app = builder.Build();

if (!configuration.GetSection("McpAuth:ApiKeys").GetChildren().Any(k => !string.IsNullOrEmpty(k.Value)))
    app.Logger.LogWarning("Nenhuma chave em McpAuth:ApiKeys — todas as chamadas a /mcp e /api-key serão rejeitadas (401).");

if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.UseSerilogRequestLogging();

// Página da raiz (wwwroot/index.html) para gerar/decodificar a API Key pelos endpoints /api-key.
// É pública: a chave MCP é digitada nela e vai no header, os endpoints continuam protegidos.
app.UseDefaultFiles();
app.UseStaticFiles();

// Antes dos UseWhen de autenticação, para o 429 vir antes do 401.
app.UseRateLimiter();

app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/mcp"),
    appBuilder => appBuilder
        .UseMiddleware<McpTrafficLoggingMiddleware>()
        .UseMiddleware<McpApiKeyMiddleware>(McpKeyFormat.Encoded));

// Os endpoints de API Key também exigem o x-api-key, mas com a chave MCP em texto puro: a key
// gerada é o que eles produzem/desfazem. Sem o middleware de tráfego, para a connection string
// (com senha) do corpo não ir para o log.
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api-key"),
    appBuilder => appBuilder.UseMiddleware<McpApiKeyMiddleware>(McpKeyFormat.Raw));

app.MapApiKeyEndpoints();

app.MapMcp("/mcp");

// Liveness para container/orquestrador — aberto, fora dos prefixos protegidos.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
