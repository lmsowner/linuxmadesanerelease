// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Collections.Concurrent;
using LinuxMadeSane.Application.Contracts.EdgeGateway;
using LinuxMadeSane.Application.Services.EdgeGateway;

namespace LinuxMadeSane.Web.Services;

public sealed class OnDemandAppLaunchCoordinator(
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime applicationLifetime,
    TimeProvider timeProvider,
    ILogger<OnDemandAppLaunchCoordinator> logger)
{
    private static readonly TimeSpan CompletedJobLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<Guid, LaunchJob> jobs = new();

    public Guid Start(OnDemandAppLaunchRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserEmail);
        RemoveExpiredJobs();

        var job = new LaunchJob(Guid.NewGuid(), request.UserId, timeProvider.GetUtcNow());
        job.Report("Preparing a secure temporary app connection.", timeProvider.GetUtcNow());
        if (!jobs.TryAdd(job.Id, job))
        {
            throw new InvalidOperationException("The app launch could not be started.");
        }

        _ = Task.Run(() => RunAsync(job, request, applicationLifetime.ApplicationStopping));
        return job.Id;
    }

    public OnDemandAppLaunchJobSnapshot? GetSnapshot(Guid jobId, string userId)
    {
        RemoveExpiredJobs();
        return jobs.TryGetValue(jobId, out var job) && job.IsOwnedBy(userId)
            ? job.Snapshot()
            : null;
    }

    public OnDemandAppLaunch? GetCompletedLaunch(Guid jobId, string userId)
    {
        RemoveExpiredJobs();
        return jobs.TryGetValue(jobId, out var job) && job.IsOwnedBy(userId)
            ? job.CompletedLaunch()
            : null;
    }

    private async Task RunAsync(
        LaunchJob job,
        OnDemandAppLaunchRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<OnDemandAppService>();
            var progress = new InlineProgress<OnDemandAppLaunchProgress>(item =>
                job.Report(item.Message, timeProvider.GetUtcNow()));
            var launch = await service.OpenAsync(
                request.ServiceKey,
                request.LeaseId,
                request.UserId,
                request.UserEmail,
                request.PublicHost,
                request.IsHttps,
                cancellationToken,
                progress);
            job.Succeed(launch, timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            job.Fail("LMS stopped while preparing the temporary app connection.", timeProvider.GetUtcNow());
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "On-Demand App launch job {LaunchJobId} failed.", job.Id);
            job.Fail(exception.Message, timeProvider.GetUtcNow());
        }
    }

    private void RemoveExpiredJobs()
    {
        var cutoff = timeProvider.GetUtcNow().Subtract(CompletedJobLifetime);
        foreach (var item in jobs.Where(item => item.Value.CanRemove(cutoff)))
        {
            jobs.TryRemove(item.Key, out _);
        }
    }

    private sealed class LaunchJob(Guid id, string userId, DateTimeOffset createdUtc)
    {
        private readonly object gate = new();
        private readonly List<OnDemandAppLaunchProgressEntry> entries = [];
        private OnDemandAppLaunch? launch;
        private string state = "running";
        private string? error;
        private DateTimeOffset updatedUtc = createdUtc;

        public Guid Id { get; } = id;

        public bool IsOwnedBy(string candidateUserId) =>
            userId.Equals(candidateUserId?.Trim(), StringComparison.Ordinal);

        public void Report(string message, DateTimeOffset timestampUtc)
        {
            lock (gate)
            {
                if (state != "running" ||
                    string.IsNullOrWhiteSpace(message) ||
                    entries.LastOrDefault()?.Message.Equals(message, StringComparison.Ordinal) == true)
                {
                    return;
                }

                entries.Add(new OnDemandAppLaunchProgressEntry(timestampUtc, message.Trim()));
                updatedUtc = timestampUtc;
            }
        }

        public void Succeed(OnDemandAppLaunch completedLaunch, DateTimeOffset timestampUtc)
        {
            lock (gate)
            {
                launch = completedLaunch;
                state = "succeeded";
                updatedUtc = timestampUtc;
            }
        }

        public void Fail(string message, DateTimeOffset timestampUtc)
        {
            lock (gate)
            {
                error = string.IsNullOrWhiteSpace(message)
                    ? "The temporary app connection could not be created."
                    : message.Trim();
                entries.Add(new OnDemandAppLaunchProgressEntry(timestampUtc, $"Failed: {error}"));
                state = "failed";
                updatedUtc = timestampUtc;
            }
        }

        public OnDemandAppLaunchJobSnapshot Snapshot()
        {
            lock (gate)
            {
                return new OnDemandAppLaunchJobSnapshot(
                    Id,
                    state,
                    entries.ToArray(),
                    state == "succeeded" ? $"/on-demand-apps/launch/{Id:D}/complete" : null,
                    error);
            }
        }

        public OnDemandAppLaunch? CompletedLaunch()
        {
            lock (gate)
            {
                return state == "succeeded" ? launch : null;
            }
        }

        public bool CanRemove(DateTimeOffset cutoff)
        {
            lock (gate)
            {
                return state != "running" && updatedUtc < cutoff;
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public sealed record OnDemandAppLaunchRequest(
    string ServiceKey,
    Guid LeaseId,
    string UserId,
    string UserEmail,
    string PublicHost,
    bool IsHttps);

public sealed record OnDemandAppLaunchProgressEntry(
    DateTimeOffset TimestampUtc,
    string Message);

public sealed record OnDemandAppLaunchJobSnapshot(
    Guid JobId,
    string State,
    IReadOnlyList<OnDemandAppLaunchProgressEntry> Entries,
    string? CompletionUrl,
    string? Error);
