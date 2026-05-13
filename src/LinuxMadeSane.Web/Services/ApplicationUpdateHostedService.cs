using Microsoft.Extensions.Options;

namespace LinuxMadeSane.Web.Services;

public sealed class ApplicationUpdateHostedService(
    ApplicationUpdateService updateService,
    IOptionsMonitor<ApplicationUpdateOptions> optionsMonitor,
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
