// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
    private const int MaxAiCommandOutputChars = 120_000;

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
        Task? connectTask = null;
        Task<ShellStream>? createStreamTask = null;
        Guid? registeredSessionId = null;

        try
        {
            connectTask = Task.Run(client.Connect, CancellationToken.None);
            await connectTask.WaitAsync(cancellationToken);
            createStreamTask = Task.Run(
                () => client.CreateShellStream("xterm-256color", (uint)request.Columns, (uint)request.Rows, 0, 0, 4096),
                CancellationToken.None);
            var stream = await createStreamTask.WaitAsync(cancellationToken);
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

            var state = new SessionState(session, host, credentials, client, stream, request.OwnerId);
            sessions[session.Id] = state;
            registeredSessionId = session.Id;

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
            if (registeredSessionId.HasValue)
            {
                sessions.TryRemove(registeredSessionId.Value, out _);
            }

            client.Dispose();
            if (connectTask is not null)
            {
                ObserveBackgroundFailure(connectTask);
            }

            if (createStreamTask is not null)
            {
                ObserveBackgroundFailure(createStreamTask);
            }

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
        var state = GetActiveSession(sessionId);

        try
        {
            EnsureSessionIsActive(state);
            if (!state.Stream.CanWrite)
            {
                MarkFaulted(state, "The SSH terminal stream is closed.");
                throw new TerminalSessionUnavailableException(
                    "The terminal connection was already closed, so the input was not sent.",
                    canRetryCommand: true,
                    connectionRecovered: false);
            }

            // ShellStream.Write(string) flushes the input itself. Keep this path
            // immediate: the Blazor circuit already dispatches terminal events in
            // order, and a service-side gate can strand every later keystroke.
            state.Stream.Write(input);
            return Task.CompletedTask;
        }
        catch (TerminalSessionUnavailableException)
        {
            throw;
        }
        catch (ObjectDisposedException exception)
        {
            MarkFaulted(state, "The SSH terminal stream closed while LMS was sending input.");
            throw CreateSendFailure(exception, writeCompleted: true);
        }
        catch (SshException exception)
        {
            MarkFaulted(state, "The SSH connection failed while LMS was sending input.");
            throw CreateSendFailure(exception, writeCompleted: true);
        }
        catch (IOException exception)
        {
            MarkFaulted(state, "The SSH terminal transport failed while LMS was sending input.");
            throw CreateSendFailure(exception, writeCompleted: true);
        }
    }

    public Task SendInterruptAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        SendInputAsync(sessionId, "\u0003", cancellationToken);

    public async Task<TerminalAiCommandResult> ExecuteAiCommandAsync(
        TerminalAiCommandRequest request,
        IProgress<CommandExecutionUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var state = GetActiveSession(request.TerminalSessionId);

        await state.AiCommandGate.WaitAsync(cancellationToken);
        try
        {
            EnsureSessionIsActive(state);
            using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                state.LifetimeCancellation.Token);
            commandCancellation.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 5, 1800)));
            var commandToken = commandCancellation.Token;
            var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? state.Session.WorkingDirectory
                : request.WorkingDirectory.Trim();
            var startedAt = DateTimeOffset.UtcNow;
            progress?.Report(new CommandExecutionStartedUpdate(request.CommandText, startedAt));

            using var client = sshConnectionFactory.CreateSshClient(
                state.Host,
                state.Credentials,
                ConnectTimeout,
                KeepAliveInterval);
            Task? connectTask = null;
            Task? commandTask = null;
            Task? inputTask = null;
            Task? stdoutTask = null;
            Task? stderrTask = null;

            try
            {
                connectTask = Task.Run(client.Connect, CancellationToken.None);
                await connectTask.WaitAsync(commandToken);

                using var command = client.CreateCommand(BuildAiRemoteCommand(request.CommandText, workingDirectory));
                command.CommandTimeout = TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 5, 1800));
                using var cancellationRegistration = commandToken.Register(() =>
                {
                    try
                    {
                        command.CancelAsync();
                    }
                    catch
                    {
                    }
                });

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();
                var asyncResult = command.BeginExecute();
                inputTask = request.StandardInput is null
                    ? Task.CompletedTask
                    : WriteAiCommandInputAsync(command.CreateInputStream(), request.StandardInput, commandToken);
                stdoutTask = PumpAiCommandStreamAsync(
                    command.OutputStream,
                    CommandExecutionOutputChannel.StandardOutput,
                    outputBuilder,
                    progress,
                    request.StandardInput,
                    commandToken);
                stderrTask = PumpAiCommandStreamAsync(
                    command.ExtendedOutputStream,
                    CommandExecutionOutputChannel.StandardError,
                    errorBuilder,
                    progress,
                    request.StandardInput,
                    commandToken);
                commandTask = Task.Run(() => command.EndExecute(asyncResult), CancellationToken.None);

                await commandTask.WaitAsync(commandToken);
                await Task.WhenAll(stdoutTask, stderrTask, inputTask).WaitAsync(commandToken);

                var completedAt = DateTimeOffset.UtcNow;
                var output = RedactSensitiveInput(outputBuilder.ToString(), request.StandardInput);
                var error = RedactSensitiveInput(errorBuilder.ToString(), request.StandardInput);
                var exitCode = command.ExitStatus ?? -1;
                progress?.Report(new CommandExecutionCompletedUpdate(exitCode, completedAt));

                return new TerminalAiCommandResult(
                    request.RequestId,
                    request.TerminalSessionId,
                    request.CommandText,
                    state.Credentials.Username,
                    workingDirectory,
                    exitCode,
                    output,
                    error,
                    startedAt,
                    completedAt);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested &&
                !state.LifetimeCancellation.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The private AI command channel stopped the command after {Math.Clamp(request.TimeoutSeconds, 5, 1800)} seconds.");
            }
            finally
            {
                if (client.IsConnected)
                {
                    client.Disconnect();
                }

                ObserveBackgroundFailure(connectTask);
                ObserveBackgroundFailure(commandTask);
                ObserveBackgroundFailure(inputTask);
                ObserveBackgroundFailure(stdoutTask);
                ObserveBackgroundFailure(stderrTask);
            }
        }
        finally
        {
            state.AiCommandGate.Release();
        }
    }

    private SessionState GetActiveSession(Guid sessionId)
    {
        if (!sessions.TryGetValue(sessionId, out var state))
        {
            throw new TerminalSessionUnavailableException(
                "The terminal session no longer exists, so the input was not sent.",
                canRetryCommand: true,
                connectionRecovered: false);
        }

        EnsureSessionIsActive(state);
        return state;
    }

    private static void EnsureSessionIsActive(SessionState state)
    {
        if (state.Session.Status != TerminalSessionStatus.Active)
        {
            throw new TerminalSessionUnavailableException(
                "The terminal session is no longer active, so the input was not sent.",
                canRetryCommand: true,
                connectionRecovered: false);
        }
    }

    private static void AbortTransport(SessionState state)
    {
        try
        {
            state.Stream.Dispose();
        }
        catch
        {
        }

        try
        {
            state.Client.Dispose();
        }
        catch
        {
        }
    }

    private static void ObserveBackgroundFailure(Task? task)
    {
        if (task is null)
        {
            return;
        }

        _ = task.ContinueWith(
            completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal static TerminalSessionUnavailableException CreateSendFailure(
        Exception exception,
        bool writeCompleted) =>
        new(
            writeCompleted
                ? "The terminal connection was lost after LMS started sending the command."
                : "The terminal connection was already closed, so the command was not sent.",
            canRetryCommand: !writeCompleted,
            connectionRecovered: false,
            exception);

    public Task ResizeAsync(Guid sessionId, int columns, int rows, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = GetActiveSession(sessionId);

        try
        {
            EnsureSessionIsActive(state);
            state.Stream.ChangeWindowSize(
                (uint)Math.Clamp(columns, 1, 1000),
                (uint)Math.Clamp(rows, 1, 1000),
                0,
                0);
            return Task.CompletedTask;
        }
        catch (ObjectDisposedException exception)
        {
            MarkFaulted(state, "The SSH terminal stream closed while LMS was resizing the console.");
            throw new TerminalSessionUnavailableException(
                "The terminal connection closed before the console could be resized.",
                canRetryCommand: false,
                connectionRecovered: false,
                exception);
        }
        catch (SshException exception)
        {
            MarkFaulted(state, "The SSH connection failed while LMS was resizing the console.");
            throw new TerminalSessionUnavailableException(
                "The terminal connection failed while resizing the console.",
                canRetryCommand: false,
                connectionRecovered: false,
                exception);
        }
    }

    public async Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!sessions.TryRemove(sessionId, out var state))
        {
            return;
        }

        state.LifetimeCancellation.Cancel();
        lock (state.SyncRoot)
        {
            state.Session = state.Session with
            {
                Status = TerminalSessionStatus.Closed,
                LastActivityUtc = DateTimeOffset.UtcNow
            };
        }

        AbortTransport(state);
        try
        {
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

    public async Task CloseOwnedSessionsAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            var ownedSessionIds = sessions
                .Where(pair => pair.Value.OwnerId == ownerId)
                .Select(pair => pair.Key)
                .ToArray();
            if (ownedSessionIds.Length == 0)
            {
                return;
            }

            foreach (var sessionId in ownedSessionIds)
            {
                await CloseSessionAsync(sessionId, cancellationToken);
            }
        }
    }

    private async Task ReadLoopAsync(SessionState state)
    {
        var buffer = new byte[4096];
        var characterBuffer = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder();

        try
        {
            while (state.Client.IsConnected && state.Stream.CanRead)
            {
                var bytesRead = await state.Stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead <= 0)
                {
                    break;
                }

                var charactersRead = decoder.GetChars(buffer, 0, bytesRead, characterBuffer, 0, flush: false);
                if (charactersRead == 0)
                {
                    continue;
                }

                var chunk = new string(characterBuffer, 0, charactersRead);

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

            MarkClosed(state);
            AbortTransport(state);
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
        state.LifetimeCancellation.Cancel();
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
        state.LifetimeCancellation.Cancel();
        TerminalSessionOutputAppended? outputAppended = null;

        lock (state.SyncRoot)
        {
            if (state.Session.Status != TerminalSessionStatus.Faulted)
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
        }

        AbortTransport(state);
        if (outputAppended is not null)
        {
            OutputAppended?.Invoke(outputAppended);
        }
    }

    private static string QuoteShellArgument(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string BuildAiRemoteCommand(string commandText, string workingDirectory) =>
        string.IsNullOrWhiteSpace(workingDirectory)
            ? commandText
            : $"cd {QuoteShellArgument(workingDirectory)} && {commandText}";

    private static async Task WriteAiCommandInputAsync(
        Stream stream,
        CommandExecutionInput input,
        CancellationToken cancellationToken)
    {
        using (stream)
        {
            var bytes = Encoding.UTF8.GetBytes(input.Content);
            await stream.WriteAsync(bytes.AsMemory(), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }

    private static async Task PumpAiCommandStreamAsync(
        Stream stream,
        CommandExecutionOutputChannel channel,
        StringBuilder builder,
        IProgress<CommandExecutionUpdate>? progress,
        CommandExecutionInput? input,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (bytesRead <= 0)
            {
                return;
            }

            var chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            AppendBounded(builder, chunk, MaxAiCommandOutputChars);
            progress?.Report(new CommandExecutionOutputUpdate(
                channel,
                RedactSensitiveInput(chunk, input),
                false,
                DateTimeOffset.UtcNow));
        }
    }

    private static void AppendBounded(StringBuilder builder, string value, int maximumLength)
    {
        builder.Append(value);
        if (builder.Length > maximumLength)
        {
            builder.Remove(0, builder.Length - maximumLength);
        }
    }

    internal static string RedactSensitiveInput(string value, CommandExecutionInput? input)
    {
        if (string.IsNullOrEmpty(value) || input is not { IsSensitive: true })
        {
            return value;
        }

        var redacted = value;
        foreach (var secret in input.Content
                     .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                     .Distinct(StringComparer.Ordinal))
        {
            redacted = redacted.Replace(secret, "[secure input redacted]", StringComparison.Ordinal);
        }

        return redacted;
    }

    private sealed class SessionState(
        TerminalSession session,
        ManagedHost host,
        ManagedHostSshCredentials credentials,
        SshClient client,
        ShellStream stream,
        Guid? ownerId)
    {
        public object SyncRoot { get; } = new();
        public TerminalSession Session { get; set; } = session;
        public ManagedHost Host { get; } = host;
        public ManagedHostSshCredentials Credentials { get; } = credentials;
        public SshClient Client { get; } = client;
        public ShellStream Stream { get; } = stream;
        public Guid? OwnerId { get; } = ownerId;
        public StringBuilder Output { get; } = new();
        public string CachedOutput { get; set; } = string.Empty;
        public bool OutputDirty { get; set; } = true;
        public long OutputRevision { get; set; }
        public Task? ReaderTask { get; set; }
        public SemaphoreSlim AiCommandGate { get; } = new(1, 1);
        public CancellationTokenSource LifetimeCancellation { get; } = new();
    }
}
