// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;

namespace LinuxMadeSane.Web.Services;

// Reapply generated policy after an update, including for persistent published
// services that never create or remove an On-Demand lease.
public sealed class EdgeGatewayConfigurationStartupService(
    IServiceScopeFactory scopeFactory,
    ILogger<EdgeGatewayConfigurationStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var routes = await scope.ServiceProvider.GetRequiredService<IEdgeGatewayStore>().ListRoutesAsync(stoppingToken);
            if (!routes.Any(route => route.Enabled)) return;

            var result = await scope.ServiceProvider.GetRequiredService<IEdgeGatewayService>()
                .ApplyCaddyConfigurationAsync(stoppingToken);
            if (!result.Success)
                logger.LogError("Could not apply Edge Gateway HTTPS-only policy at startup: {Summary}", result.Summary);
            else
                logger.LogInformation("Applied Edge Gateway HTTPS-only policy to existing published services");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not reconcile the existing Edge Gateway configuration at startup");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
