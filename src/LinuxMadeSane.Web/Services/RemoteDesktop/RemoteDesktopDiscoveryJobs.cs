// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Collections.Concurrent;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RemoteDesktop;

namespace LinuxMadeSane.Web.Services.RemoteDesktop;

public sealed record RemoteDesktopDiscoveredCandidate(RemoteDesktopCandidate Candidate, bool Checked);
public sealed record RemoteDesktopDiscoverySnapshot(
    IReadOnlyList<RemoteDesktopDiscoveredCandidate> Candidates, bool Running, string Message, bool Failed);

// Async network jobs outlive the Hosts component. Cache entries are isolated by
// LMS owner and discovery scope, and are never treated as trusted connections.
public sealed class RemoteDesktopDiscoveryJobs(IServiceScopeFactory scopes, ILogger<RemoteDesktopDiscoveryJobs> logger) : IDisposable
{
    private readonly ConcurrentDictionary<(Guid, SshHostDiscoveryScope), Job> jobs = new();
    private readonly CancellationTokenSource shutdown = new();

    public SshHostDiscoveryScope LatestScope(Guid owner) => jobs.Where(x => x.Key.Item1 == owner)
        .OrderByDescending(x => x.Value.Started).Select(x => x.Key.Item2).FirstOrDefault();

    public RemoteDesktopDiscoverySnapshot Snapshot(Guid owner, SshHostDiscoveryScope scope)
    {
        if (!jobs.TryGetValue((owner, scope), out var job)) return new([], false, "", false);
        lock (job) return new(job.Entries.Values.OrderBy(x => x.Candidate.Hostname).ThenBy(x => x.Candidate.Port).ToArray(),
            job.Running, job.Message, job.Failed);
    }

    public void Start(Guid owner, SshHostDiscoveryScope scope)
    {
        var job = jobs.GetOrAdd((owner, scope), _ => new());
        lock (job)
        {
            if (job.Running) return;
            job.Running = true; job.Failed = false; job.Started = DateTimeOffset.UtcNow;
            job.Message = "Discovering remote desktops; rechecking cached candidates…";
            foreach (var key in job.Entries.Keys.ToArray()) job.Entries[key] = job.Entries[key] with { Checked = false };
            job.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            job.Work = Task.Run(() => RunAsync(job, scope));
        }
    }

    public void Cancel(Guid owner, SshHostDiscoveryScope scope)
    {
        if (jobs.TryGetValue((owner, scope), out var job)) lock (job) job.Cancellation?.Cancel();
    }

    private async Task RunAsync(Job job, SshHostDiscoveryScope discoveryScope)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var discovery = scope.ServiceProvider.GetRequiredService<ISshHostDiscoveryService>();
            var found = await discovery.DiscoverRemoteDesktopsAsync(discoveryScope,
                new InlineReport<string>(text => { lock (job) job.Message = text; }),
                new InlineReport<RemoteDesktopProbeResult>(result =>
                {
                    lock (job)
                    {
                        var endpoint = (result.Hostname, result.Port);
                        if (result.Candidate is null) job.Entries.Remove(endpoint);
                        else job.Entries[endpoint] = new(result.Candidate, true);
                    }
                }), job.Cancellation!.Token);
            lock (job)
            {
                // Also removes cached endpoints outside today's authorised ranges.
                job.Entries.Clear();
                foreach (var candidate in found) job.Entries[(candidate.Hostname, candidate.Port)] = new(candidate, true);
                job.Message = $"Discovery complete: {job.Entries.Count} candidates. Review each before adding it.";
            }
        }
        catch (OperationCanceledException) { lock (job) job.Message = "Discovery cancelled; results already checked are retained."; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Remote desktop discovery failed.");
            lock (job) { job.Failed = true; job.Message = "Discovery failed: " + exception.Message; }
        }
        finally
        {
            lock (job) { job.Running = false; job.Cancellation?.Dispose(); job.Cancellation = null; }
        }
    }

    public void Dispose() { shutdown.Cancel(); shutdown.Dispose(); }
    private sealed class InlineReport<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
    private sealed class Job
    {
        public readonly Dictionary<(string, int), RemoteDesktopDiscoveredCandidate> Entries = new();
        public bool Running, Failed;
        public string Message = "";
        public DateTimeOffset Started;
        public CancellationTokenSource? Cancellation;
        public Task? Work;
    }
}
