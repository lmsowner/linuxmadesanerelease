// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

/// <summary>
/// Invalidates temporary Edge Gateway approval state whenever the LMS process starts.
/// Temporary access is deliberately process-lifetime state; a restart/update must
/// require a fresh approval email rather than silently preserving an old grant.
/// </summary>
public sealed class EdgeGatewayTemporaryIpApprovalStartupResetHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<EdgeGatewayTemporaryIpApprovalStartupResetHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var resetAtUtc = DateTimeOffset.UtcNow;
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IEdgeGatewayTemporaryIpApprovalStore>();
        await store.ResetTransientStateAsync(resetAtUtc, cancellationToken);
        logger.LogInformation("Reset transient Edge Gateway email approval state after LMS startup.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
