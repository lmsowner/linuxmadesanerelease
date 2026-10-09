// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using System.Collections.Concurrent;
using System.Text.Json;
using LinuxMadeSane.Application.Contracts.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace LinuxMadeSane.Infrastructure.Services.SshForwards;
public sealed class SshForwardSupervisor(SshForwardStore store, ISshForwardProcessFactory processes,
    ISshForwardCredentialSource credentials, ILogger<SshForwardSupervisor> logger) : BackgroundService
{
    private sealed record Worker(string Revision, CancellationTokenSource Stop, Task Task);
    private readonly SemaphoreSlim workerGate = new(1, 1);
    private readonly Dictionary<Guid, Worker> workers = [];
    private readonly ConcurrentDictionary<Guid, SshForwardStatus> statuses = new();
    private readonly ConcurrentDictionary<Guid, int> generations = new();
    public SshForwardStatus Status(Guid id, bool enabled) => !enabled ? new("Stopped", "Automatic reconnect is paused.") :
        statuses.GetValueOrDefault(id) ?? new("Queued", "Preparing this SSH connection.");
    public void Restart(Guid id) => generations.AddOrUpdate(id, 1, (_, previous) => previous + 1);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await ReconcileAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogError(exception, "SSH forward definitions could not be read; existing connections were retained."); }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var worker in workers.Values) worker.Stop.Cancel();
            await Task.WhenAll(workers.Values.Select(x => x.Task));
            foreach (var worker in workers.Values) worker.Stop.Dispose(); workers.Clear();
        }
    }
    public async Task StopForwardAsync(Guid id, CancellationToken token)
    {
        await workerGate.WaitAsync(token);
        try { await RemoveWorkerAsync(id); }
        finally { workerGate.Release(); }
    }
    internal async Task ReconcileAsync(CancellationToken token)
    {
        await workerGate.WaitAsync(token);
        try { await ReconcileCoreAsync(token); }
        finally { workerGate.Release(); }
    }
    private async Task ReconcileCoreAsync(CancellationToken token)
    {
        var rules = await store.ReadAsync(token);
        foreach (var id in workers.Keys.Where(id => rules.All(rule => rule.Id != id || !rule.Enabled)).ToArray()) await RemoveWorkerAsync(id);
        foreach (var rule in rules.Where(x => x.Enabled))
        {
            var revision = SshForwardDefinition.RuntimeRevision(rule) + await credentials.RevisionAsync(rule.CredentialId, token) + generations.GetValueOrDefault(rule.Id);
            if (workers.TryGetValue(rule.Id, out var current) && current.Revision == revision && !current.Task.IsCompleted) continue;
            await RemoveWorkerAsync(rule.Id);
            var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            workers[rule.Id] = new(revision, stop, RunForwardAsync(rule, stop.Token));
        }
    }
    private async Task RemoveWorkerAsync(Guid id)
    {
        if (!workers.Remove(id, out var worker)) return;
        worker.Stop.Cancel(); await worker.Task; worker.Stop.Dispose(); statuses.TryRemove(id, out _);
    }
    private async Task RunForwardAsync(SshPortForward input, CancellationToken token)
    {
        var retries = 0; DateTimeOffset? lastFailure = null; string? lastFailureDetail = null;
        while (!token.IsCancellationRequested)
        {
            ISshForwardProcess? process = null;
            try
            {
                var rule = SshForwardDefinition.Validate(input);
                statuses[rule.Id] = new("Connecting", $"Connecting to {rule.Username}@{rule.Server}:{rule.SshPort}.", null, lastFailure, retries, LastFailureDetail: lastFailureDetail);
                var credential = await credentials.ResolveAsync(rule.CredentialId, token);
                SshPortForwardService.ValidateCredential(rule, credential);
                process = await processes.StartAsync(rule, credential, token);
                var deadline = DateTimeOffset.UtcNow.AddSeconds(35); DateTimeOffset? connected = null;
                while (!process.HasExited)
                {
                    // The control socket establishes readiness, not transport health. A busy
                    // host can delay its reply while the tunnel is working. Once connected,
                    // let OpenSSH's encrypted keepalives detect failure and exit naturally.
                    var alive = connected is not null || await process.IsConnectedAsync(token);
                    if (alive)
                    {
                        connected ??= DateTimeOffset.UtcNow;
                        statuses[rule.Id] = new("Connected", process.TrafficError is { } traffic ? "SSH remains connected. Last forwarding error: " + ExplainFailure(traffic, rule) : "SSH connection is active. Destination application health is separate.", connected, lastFailure, retries, process.ProcessId, process.AllocatedListenPort, lastFailureDetail);
                    }
                    else if (DateTimeOffset.UtcNow > deadline)
                        throw new InvalidOperationException("SSH did not establish a usable connection. Check credentials, the remote forwarding policy and the listen endpoint.");
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                }
                throw new InvalidOperationException(ExplainFailure(process.Failure, rule));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                lastFailure = DateTimeOffset.UtcNow;
                lastFailureDetail = exception.Message;
                logger.LogWarning("SSH forward {ForwardId} ({Name}) disconnected: {Reason}. Retrying in {Delay} seconds.", input.Id, input.Name, lastFailureDetail, RetryDelay(input, retries));
                statuses[input.Id] = new("Retrying", lastFailureDetail + $" Retrying in {RetryDelay(input, retries)} seconds.", null, lastFailure, retries, LastFailureDetail: lastFailureDetail);
            }
            finally { if (process is not null) await process.DisposeAsync(); }
            try { await Task.Delay(TimeSpan.FromSeconds(RetryDelay(input, retries)), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            retries++;
        }
    }
    private static int RetryDelay(SshPortForward rule, int retries) => (int)Math.Min(300, Math.Clamp(rule.RetrySeconds, 1, 300) * Math.Pow(2, Math.Min(retries, 8)));
    public static string ExplainFailure(string error, SshPortForward rule)
    {
        var location = $"On {rule.Server}, for {rule.Username}";
        if (error.Contains("REMOTE HOST IDENTIFICATION", StringComparison.OrdinalIgnoreCase) || error.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase))
            return $"The saved identity for {rule.Server} changed. Verify the server fingerprint before updating LMS's trusted host key. Detail: {error}";
        if (rule.IsRemote && rule.ListenKind == SshForwardEndpointKind.UnixSocket && error.Contains("forwarding failed", StringComparison.OrdinalIgnoreCase))
            return $"{location}: the remote Unix socket listener was refused. Check its parent-folder permissions, AllowStreamLocalForwarding, and StreamLocalBindUnlink if an old socket remains. Detail: {error}";
        if (error.Contains("remote port forwarding failed", StringComparison.OrdinalIgnoreCase) || error.Contains("administratively prohibited", StringComparison.OrdinalIgnoreCase))
            return $"{location}: SSH forwarding was refused. Check AllowTcpForwarding/AllowStreamLocalForwarding, PermitListen/PermitOpen, and GatewayPorts for a non-local remote listener. Detail: {error}";
        if (error.Contains("connect failed", StringComparison.OrdinalIgnoreCase))
            return $"The destination could not be reached from {(rule.IsRemote ? "this LMS host" : rule.Server)}. Check the destination service and its network access. Detail: {error}";
        if (error.Contains("bind", StringComparison.OrdinalIgnoreCase) && error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return "The listener cannot bind on this LMS host. Use an unprivileged port (1024 or higher), or grant the LMS service permission to bind that port. Detail: " + error;
        if (error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return $"{location}: SSH authentication was refused. Test the selected saved credential and check the user's allowed login methods. Detail: {error}";
        if (error.Contains("Address already in use", StringComparison.OrdinalIgnoreCase) || error.Contains("cannot listen", StringComparison.OrdinalIgnoreCase))
            return $"The listener is unavailable on {(rule.IsRemote ? rule.Server : "this LMS host")}. Choose an unused port or socket and check permissions. Detail: {error}";
        return $"SSH connection to {rule.Server}:{rule.SshPort} failed. {error}";
    }
}
