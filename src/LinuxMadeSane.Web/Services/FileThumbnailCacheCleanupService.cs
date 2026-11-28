// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Web.Services;

public sealed class FileThumbnailCacheCleanupService(
    FileThumbnailCacheService cacheService,
    FileThumbnailCacheOptions options,
    ILogger<FileThumbnailCacheCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PurgeAsync(stoppingToken);

        using var timer = new PeriodicTimer(options.CleanupInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await PurgeAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await cacheService.PurgeExpiredAndTrimAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Scheduled file thumbnail cache cleanup failed.");
        }
    }
}
