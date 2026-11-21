// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using System.Net;
using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class ServiceWatchdog(LinuxMadeSaneDbContext db) : IServiceWatchdog
{
    internal const string Prefix = "service-watchdog:";
    private static string Key(string unit)
    {
        if (!Regex.IsMatch(unit, @"\A[A-Za-z0-9_][A-Za-z0-9_.:@\\-]*\.service\z"))
            throw new ArgumentException("Select a single system service unit.");
        return Prefix + unit;
    }

    public async Task<ServiceWatchdogStatus> GetAsync(string unitName, CancellationToken cancellationToken = default)
    {
        var key = Key(unitName);
        var row = await db.InfrastructureStates.AsNoTracking().SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        return row is null ? new(new(unitName)) : JsonSerializer.Deserialize<ServiceWatchdogStatus>(row.Json)!;
    }

    public async Task SaveAsync(ServiceWatchdogPolicy policy, CancellationToken cancellationToken = default)
    {
        var recipient = policy.EmailRecipient.Trim();
        if (recipient.Length > 0 && (!MailAddress.TryCreate(recipient, out var address) || address.Address != recipient))
            throw new ArgumentException("Enter one valid notification email address.");
        var status = await GetAsync(policy.UnitName, cancellationToken);
        await StoreAsync(status with { Policy = policy with { EmailRecipient = recipient },
            Attempts = 0, LastAttemptUtc = null, Message = policy.Enabled ? "Monitoring enabled; waiting for next check." : "Monitoring disabled." }, cancellationToken);
    }

    // Compare the complete previous record so background progress cannot overwrite a user's
    // concurrent disable/edit, or another worker's claimed retry.
    internal async Task<bool> TryUpdateAsync(ServiceWatchdogStatus status, string previousJson, CancellationToken ct)
    {
        var key = Key(status.Policy.UnitName);
        var json = JsonSerializer.Serialize(status);
        return await db.InfrastructureStates.Where(x => x.Key == key && x.Json == previousJson)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Json, json)
                .SetProperty(x => x.UpdatedAtUtc, DateTimeOffset.UtcNow), ct) == 1;
    }

    internal async Task StoreAsync(ServiceWatchdogStatus status, CancellationToken ct)
    {
        var key = Key(status.Policy.UnitName);
        var row = await db.InfrastructureStates.FindAsync([key], ct);
        if (row is null) { row = new InfrastructureStateEntity { Key = key }; db.InfrastructureStates.Add(row); }
        row.Json = JsonSerializer.Serialize(status);
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

public sealed class ServiceWatchdogHostedService(IServiceScopeFactory scopes, ILogger<ServiceWatchdogHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await TickAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogError(ex, "Service watchdog check failed"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LinuxMadeSaneDbContext>();
        var store = new ServiceWatchdog(db);
        var policies = await db.InfrastructureStates.AsNoTracking().Where(x => x.Key.StartsWith(ServiceWatchdog.Prefix)).Select(x => x.Json).ToListAsync(ct);
        var services = scope.ServiceProvider.GetRequiredService<ILinuxServiceModuleDataService>();
        if (!policies.Any(json => JsonSerializer.Deserialize<ServiceWatchdogStatus>(json)?.Policy.Enabled == true)) return;
        var liveServices = await services.ListServicesAsync(ct);
        foreach (var json in policies)
        {
            try
            {
                var status = JsonSerializer.Deserialize<ServiceWatchdogStatus>(json)!;
                if (!status.Policy.Enabled) continue;
                var service = liveServices.SingleOrDefault(x => x.UnitName == status.Policy.UnitName);
                if (service is null || service.IsRemoved || service.UnitFileState.Contains("masked", StringComparison.OrdinalIgnoreCase)) continue;
                if (service.RuntimeState == ServiceRuntimeState.Running)
                {
                    if (status.IncidentOpen)
                    {
                        status = status with { IncidentOpen = false, Attempts = 0, LastAttemptUtc = null, Message = "Service recovered." };
                        if (!await store.TryUpdateAsync(status, json, ct)) continue;
                        await NotifyAsync(scope.ServiceProvider, status, ct);
                    }
                    continue;
                }
                // Inactive includes deliberate operator stops and successful one-shot services.
                if (service.ActiveState != "failed" || status.Attempts >= 5) continue;
                var now = DateTimeOffset.UtcNow;
                if (status.LastAttemptUtc is { } last && now - last < TimeSpan.FromSeconds(30 * Math.Pow(2, status.Attempts))) continue;
                status = status with { IncidentOpen = true, Attempts = status.Attempts + 1, LastAttemptUtc = now,
                    Message = "Service failed; attempting restart." };
                // Persist before the command so a process restart cannot reset the retry budget.
                if (!await store.TryUpdateAsync(status, json, ct)) continue;
                var pendingJson = JsonSerializer.Serialize(status);
                var current = await store.GetAsync(status.Policy.UnitName, ct);
                if (!current.Policy.Enabled || JsonSerializer.Serialize(current) != pendingJson) continue;
                var result = await services.ControlServiceAsync(service.Id, ServiceControlAction.Restart, ct);
                status = status with { Message = $"Restart attempt {status.Attempts}/5: {result.Message}" +
                    (status.Attempts == 5 ? " Automatic retries exhausted; inspect the service logs." : " Waiting for live recovery check.") };
                if (!await store.TryUpdateAsync(status, pendingJson, ct)) continue;
                if (status.Attempts is 1 or 5) await NotifyAsync(scope.ServiceProvider, status, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "Service watchdog policy failed"); }
        }
    }

    private async Task NotifyAsync(IServiceProvider provider, ServiceWatchdogStatus status, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(status.Policy.EmailRecipient)) return;
        var email = provider.GetRequiredService<IMessagingEmailSettingsStore>();
        if (!(await email.GetAsync(ct)).IsEnabled) return;
        var result = await provider.GetRequiredService<IEmailDeliveryService>().SendHtmlAsync(status.Policy.EmailRecipient,
            $"LMS {Environment.MachineName}: {status.Policy.UnitName}",
            $"<p>{WebUtility.HtmlEncode(Environment.MachineName)}: {WebUtility.HtmlEncode(status.Policy.UnitName)}</p><p>{WebUtility.HtmlEncode(status.Message)}</p>", ct);
        if (!result.Succeeded) logger.LogWarning("Watchdog email delivery failed: {Message}", result.Message);
    }
}
