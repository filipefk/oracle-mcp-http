using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace McpToolkit;

public sealed class OracleIdleTimeoutService(OracleConnectionManager connectionManager, IOptions<McpOptions> options) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timeout = TimeSpan.FromMinutes(options.Value.IdleTimeoutMinutes);
        if (timeout <= TimeSpan.Zero) return;

        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await connectionManager.CloseIdleAsync(timeout, stoppingToken);
    }
}
