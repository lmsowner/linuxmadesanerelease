// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.EdgeGateway;
using LinuxMadeSane.Application.Services.EdgeGateway;

namespace LinuxMadeSane.Web.Services;

public sealed class OnDemandAppCleanupHostedService(
    IServiceScopeFactory scopeFactory,
    OnDemandAppsOptions options,
    ILogger<OnDemandAppCleanupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CleanupAsync(stoppingToken);
        using var timer = new PeriodicTimer(options.CleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CleanupAsync(stoppingToken);
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OnDemandAppService>()
                .CleanupStaleAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "On-Demand App stale route cleanup failed");
        }
    }
}
