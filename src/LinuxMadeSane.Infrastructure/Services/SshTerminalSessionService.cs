// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Collections.Concurrent;
using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SshTerminalSessionService(
    ILogger<SshTerminalSessionService> logger,
    ManagedHostSshConnectionFactory sshConnectionFactory) : ITerminalSessionService
{
    private readonly ConcurrentDictionary<Guid, SessionState> sessions = new();
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    public event Action<TerminalSessionOutputAppended>? OutputAppended;

    public async Task<TerminalSession> StartSessionAsync(
        ManagedHost host,
        TerminalConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var credentials = await sshConnectionFactory.ResolveCredentialsAsync(
            host,
            new ManagedHostSshCredentialRequest(
                request.Username,
                request.Password,
                request.PrivateKey,
                request.PrivateKeyPassphrase,
                request.PreferStoredCredentials),
            cancellationToken);

        var client = sshConnectionFactory.CreateSshClient(host, credentials, ConnectTimeout, KeepAliveInterval);

        try
        {
            client.Connect();
            var stream = client.CreateShellStream("xterm-256color", (uint)request.Columns, (uint)request.Rows, 0, 0, 4096);
            var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? host.DefaultWorkingDirectory
                : request.WorkingDirectory.Trim();

            var session = new TerminalSession(
                Guid.NewGuid(),
                host.Id,
                TerminalSessionStatus.Active,
                workingDirectory,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);

            var state = new SessionState(session, client, stream);
            sessions[session.Id] = state;

            state.ReaderTask = Task.Run(() => ReadLoopAsync(state), CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                stream.Write($"cd {QuoteShellArgument(workingDirectory)}\n");
                stream.Flush();
            }

            logger.LogInformation("Started SSH terminal session {SessionId} for host {HostId}", session.Id, host.Id);

            return session;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public Task<TerminalSessionSnapshot?> GetSnapshotAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!sessions.TryGetValue(sessionId, out var state))
        {
            return Task.FromResult<TerminalSessionSnapshot?>(null);
        }

        lock (state.SyncRoot)
        {
            if (state.OutputDirty)
            {
                state.CachedOutput = state.Output.ToString();
                state.OutputDirty = false;
            }

            var snapshot = new TerminalSessionSnapshot(
                state.Session.Id,
                state.Session.Status,
                state.Session.WorkingDirectory,
                state.CachedOutput,
                state.OutputRevision,
                state.Session.StartedAtUtc,
                state.Session.LastActivityUtc);

            return Task.FromResult<TerminalSessionSnapshot?>(snapshot);
        }
    }

    public Task SendInputAsync(Guid sessionId, string input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!sessions.TryGetValue(sessionId, out var state))
        {
            throw new InvalidOperationException("Terminal session was not found.");
        }

        if (state.Session.Status != TerminalSessionStatus.Active)
        {
            throw new InvalidOperationException("Terminal session is not active.");
        }

        state.Stream.Write(input);
        state.Stream.Flush();
        return Task.CompletedTask;
    }

    public Task ResizeAsync(Guid sessionId, int columns, int rows, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!sessions.TryRemove(sessionId, out var state))
        {
            return;
        }

        try
        {
            lock (state.SyncRoot)
            {
                state.Session = state.Session with
                {
                    Status = TerminalSessionStatus.Closed,
                    LastActivityUtc = DateTimeOffset.UtcNow
                };
            }

            state.Stream.Dispose();
            state.Client.Disconnect();
            state.Client.Dispose();

            if (state.ReaderTask is not null)
            {
                await state.ReaderTask.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Terminal session {SessionId} closed with cleanup warnings", sessionId);
        }
    }

    private async Task ReadLoopAsync(SessionState state)
    {
        var buffer = new byte[4096];

        try
        {
            while (state.Client.IsConnected && state.Stream.CanRead)
            {
                var bytesRead = await state.Stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead <= 0)
                {
                    await Task.Delay(40);
                    continue;
                }

                var chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                lock (state.SyncRoot)
                {
                    state.Output.Append(chunk);
                    if (state.Output.Length > 120_000)
                    {
                        state.Output.Remove(0, state.Output.Length - 120_000);
                    }

                    state.OutputDirty = true;
                    state.OutputRevision++;
                    state.Session = state.Session with
                    {
                        Status = TerminalSessionStatus.Active,
                        LastActivityUtc = DateTimeOffset.UtcNow
                    };
                }

                OutputAppended?.Invoke(new TerminalSessionOutputAppended(
                    state.Session.Id,
                    chunk,
                    state.OutputRevision,
                    state.Session.Status,
                    state.Session.LastActivityUtc));
            }
        }
        catch (SshException exception)
        {
            logger.LogWarning(exception, "SSH stream failed for terminal session {SessionId}", state.Session.Id);
            MarkFaulted(state, exception.Message);
        }
        catch (ObjectDisposedException)
        {
            MarkClosed(state);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Terminal reader loop failed for session {SessionId}", state.Session.Id);
            MarkFaulted(state, exception.Message);
        }
    }

    private void MarkClosed(SessionState state)
    {
        lock (state.SyncRoot)
        {
            state.Session = state.Session with
            {
                Status = TerminalSessionStatus.Closed,
                LastActivityUtc = DateTimeOffset.UtcNow
            };
        }
    }

    private void MarkFaulted(SessionState state, string error)
    {
        TerminalSessionOutputAppended? outputAppended = null;

        lock (state.SyncRoot)
        {
            var chunk = $"{Environment.NewLine}[terminal error] {error}{Environment.NewLine}";
            state.Output.Append(chunk);
            state.OutputDirty = true;
            state.OutputRevision++;
            state.Session = state.Session with
            {
                Status = TerminalSessionStatus.Faulted,
                LastActivityUtc = DateTimeOffset.UtcNow
            };

            outputAppended = new TerminalSessionOutputAppended(
                state.Session.Id,
                chunk,
                state.OutputRevision,
                state.Session.Status,
                state.Session.LastActivityUtc);
        }

        if (outputAppended is not null)
        {
            OutputAppended?.Invoke(outputAppended);
        }
    }

    private static string QuoteShellArgument(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private sealed class SessionState(TerminalSession session, SshClient client, ShellStream stream)
    {
        public object SyncRoot { get; } = new();
        public TerminalSession Session { get; set; } = session;
        public SshClient Client { get; } = client;
        public ShellStream Stream { get; } = stream;
        public StringBuilder Output { get; } = new();
        public string CachedOutput { get; set; } = string.Empty;
        public bool OutputDirty { get; set; } = true;
        public long OutputRevision { get; set; }
        public Task? ReaderTask { get; set; }
    }
}
