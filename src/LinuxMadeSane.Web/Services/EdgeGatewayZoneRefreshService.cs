// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Application.Interfaces;

namespace LinuxMadeSane.Web.Services;

public sealed class EdgeGatewayZoneRefreshService(IServiceScopeFactory scopes, ILogger<EdgeGatewayZoneRefreshService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IEdgeGatewayService>().RefreshZoneDirectoryAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            { log.LogWarning(ex, "Could not refresh the Edge Gateway zone directory"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
