// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Globalization;
using LinuxMadeSane.Application.Contracts.Updates;
using LinuxMadeSane.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class HostSystemUpdateScheduleHostedService(
    IServiceScopeFactory scopeFactory,
    IHostSystemUpdateService updateService,
    TimeProvider timeProvider,
    ILogger<HostSystemUpdateScheduleHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Scheduled host update tick failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var schedule = await updateService.GetScheduleAsync(cancellationToken);
        if (!schedule.Enabled || !schedule.ApplyPackageUpdates)
        {
            return;
        }

        var localNow = timeProvider.GetLocalNow();
        if (localNow.Hour != schedule.HourLocal || localNow.Minute != schedule.MinuteLocal)
        {
            return;
        }

        var dayKey = localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (string.Equals(schedule.LastRunDayKey, dayKey, StringComparison.Ordinal))
        {
            return;
        }

        logger.LogInformation(
            "Starting scheduled host package updates at {Hour:00}:{Minute:00} local.",
            schedule.HourLocal,
            schedule.MinuteLocal);

        var mode = schedule.UseDistUpgrade
            ? HostPackageUpdateMode.DistUpgrade
            : schedule.SecurityOnly
                ? HostPackageUpdateMode.SecurityOnly
                : HostPackageUpdateMode.AllPackages;
        var result = await updateService.ApplyPackageUpdatesAsync(mode, schedule.RebootIfRequired, cancellationToken);
        var summary = result.Job.State == HostSystemUpdateJobState.Failed
            ? result.Job.Summary
            : $"{result.Job.Summary} ({result.UpgradeableCount} remaining)";

        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IHostUpdateScheduleStore>();
        await store.SaveAsync(
            schedule with
            {
                LastRunAtUtc = timeProvider.GetUtcNow(),
                LastRunSummary = summary,
                LastRunDayKey = dayKey,
                UpdatedAtUtc = timeProvider.GetUtcNow()
            },
            cancellationToken);
    }
}
