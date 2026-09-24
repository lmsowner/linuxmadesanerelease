// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using Microsoft.Extensions.Options;

namespace LinuxMadeSane.Web.Services;

public sealed class ApplicationUpdateHostedService(
    ApplicationUpdateService updateService,
    IOptionsMonitor<ApplicationUpdateOptions> optionsMonitor,
    IConfiguration configuration,
    IHostEnvironment environment,
    IHostApplicationLifetime applicationLifetime,
    ILogger<ApplicationUpdateHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = optionsMonitor.CurrentValue;
            var delay = TimeSpan.FromMinutes(Math.Clamp(options.CheckIntervalMinutes, 15, 10_080));

            try
            {
                if (applicationLifetime.ApplicationStarted.IsCancellationRequested &&
                    (options.Edition.Equals("community", StringComparison.OrdinalIgnoreCase) ||
                     options.Edition.Equals("ce", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        CommunityReleaseRetention.Trim(AppContext.BaseDirectory, environment.ContentRootPath, configuration, logger);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "CE release cleanup failed; it will be retried at the next update check.");
                    }
                }

                if (options.Enabled)
                {
                    var status = await updateService.CheckForUpdatesAsync(stoppingToken);
                    if (options.InstallAutomatically && status.IsUpdateAvailable)
                    {
                        await updateService.InstallLatestAsync(stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Scheduled Linux Made Sane update check failed.");
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
