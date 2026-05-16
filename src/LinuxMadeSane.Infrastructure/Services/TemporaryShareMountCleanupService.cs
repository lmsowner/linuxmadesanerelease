// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

internal sealed class TemporaryShareMountCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<TemporaryShareMountCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var shareDataService = scope.ServiceProvider.GetRequiredService<ILinuxShareModuleDataService>();
            await shareDataService.CleanupTemporaryRemoteMountsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Temporary remote share mount cleanup failed during LMS startup.");
        }
    }
}
