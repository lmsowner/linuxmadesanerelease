// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SshTerminalSessionService(
    ILogger<SshTerminalSessionService> logger,
    ManagedHostSshConnectionFactory sshConnectionFactory,
    IDataProtectionProvider? dataProtectionProvider = null) : ITerminalSessionService
{
    private readonly ConcurrentDictionary<Guid, SessionState> sessions = new();
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SessionSetupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    private const int MaxAiCommandOutputChars = 120_000;

    public event Action<TerminalSessionOutputAppended>? OutputAppended;

    public async Task<TerminalSession> StartSessionAsync(
        ManagedHost host,
        TerminalConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var setupStarted = Stopwatch.GetTimestamp();
        using var setupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        setupCancellation.CancelAfter(SessionSetupTimeout);
        var setupToken = setupCancellation.Token;
        void Report(string message) => request.Progress?.Invoke(message);
        Report($"Configured SSH target: {host.Hostname.Trim()}:{host.Port} as {request.Username}. SSH.NET uses this host setting; the OS chooses the route.");
        _ = ReportDnsCandidatesAsync(host.Hostname.Trim(), request.Progress, cancellationToken);
        Report("Resolving SSH credentials");
        logger.LogInformation("SSH terminal setup started for host {HostId} target {Hostname}:{Port} as {Username}",
            host.Id, host.Hostname.Trim(), host.Port, request.Username);
        ManagedHostSshCredentials credentials;
        try
        {
            credentials = await sshConnectionFactory.ResolveCredentialsAsync(
                host,
                new ManagedHostSshCredentialRequest(
                    request.Username,
                    request.Password,
                    request.PrivateKey,
                    request.PrivateKeyPassphrase,
                    request.PreferStoredCredentials),
                setupToken);
        }
        catch (Exception exception)
        {
            Report($"Credential resolution failed: {exception.Message}");
            logger.LogWarning(exception,
                "SSH terminal credential resolution failed for host {HostId} as {Username} after {ElapsedMs} ms",
                host.Id, request.Username, Stopwatch.GetElapsedTime(setupStarted).TotalMilliseconds);
            throw;
        }
        var credentialsResolved = Stopwatch.GetTimestamp();
        Report($"SSH credentials ready for {credentials.Username} ({Stopwatch.GetElapsedTime(setupStarted).TotalMilliseconds:0} ms)");
        logger.LogInformation("SSH terminal credentials resolved for host {HostId} as {Username} after {ElapsedMs} ms",
            host.Id, credentials.Username, Stopwatch.GetElapsedTime(setupStarted).TotalMilliseconds);

        var client = sshConnectionFactory.CreateSshClient(host, credentials, ConnectTimeout, KeepAliveInterval);
        byte[] remoteHostKey = [];
        client.HostKeyReceived += (_, args) => remoteHostKey = args.HostKey.ToArray();
        var setupStage = "SSH handshake";
        Task? connectTask = null;
        Task<ShellStream>? createStreamTask = null;
        Guid? registeredSessionId = null;

        try
        {
            Report("Opening SSH TCP connection and authenticating");
            logger.LogInformation("SSH terminal starting handshake for host {HostId} as {Username}", host.Id, credentials.Username);
            connectTask = Task.Run(client.Connect, CancellationToken.None);
            await connectTask.WaitAsync(setupToken);
            var sshConnected = Stopwatch.GetTimestamp();
            Report($"SSH handshake and authentication complete ({Stopwatch.GetElapsedTime(credentialsResolved, sshConnected).TotalMilliseconds:0} ms)");
            logger.LogInformation("SSH terminal handshake completed for host {HostId} as {Username} after {ElapsedMs} ms",
                host.Id, credentials.Username, Stopwatch.GetElapsedTime(credentialsResolved, sshConnected).TotalMilliseconds);
            setupStage = "working directory selection";
            var useConnectedUserHome = ShouldUseConnectedUserHome(host, request, credentials.Username);
            var workingDirectory = useConnectedUserHome
                ? string.Empty
                : string.IsNullOrWhiteSpace(request.WorkingDirectory)
                    ? host.DefaultWorkingDirectory
                    : request.WorkingDirectory.Trim();
            var workingDirectoryResolved = Stopwatch.GetTimestamp();
            Report(useConnectedUserHome
                ? "Using the connected user's shell home directory"
                : $"Working directory selected: {workingDirectory}");
            logger.LogInformation("SSH terminal working directory selected for host {HostId} as {Username} after {ElapsedMs} ms",
                host.Id, credentials.Username, Stopwatch.GetElapsedTime(sshConnected, workingDirectoryResolved).TotalMilliseconds);
            setupStage = "shell creation";
            Report("Requesting interactive SSH shell and PTY");
            logger.LogInformation("SSH terminal opening shell for host {HostId} as {Username}", host.Id, credentials.Username);
            createStreamTask = Task.Run(
                () => client.CreateShellStream("xterm-256color", (uint)request.Columns, (uint)request.Rows, 0, 0, 4096),
                CancellationToken.None);
            var stream = await createStreamTask.WaitAsync(setupToken);
            var shellOpened = Stopwatch.GetTimestamp();
            Report($"SSH shell opened ({Stopwatch.GetElapsedTime(workingDirectoryResolved, shellOpened).TotalMilliseconds:0} ms); waiting for shell output");
            logger.LogInformation("SSH terminal shell opened for host {HostId} as {Username} after {ElapsedMs} ms",
                host.Id, credentials.Username, Stopwatch.GetElapsedTime(workingDirectoryResolved, shellOpened).TotalMilliseconds);
            var session = new TerminalSession(
                Guid.NewGuid(),
                host.Id,
                TerminalSessionStatus.Active,
                workingDirectory,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)
            {
                Username = credentials.Username
            };

            var state = new SessionState(session, host, credentials, client, stream, request.OwnerId, remoteHostKey);
            sessions[session.Id] = state;
            registeredSessionId = session.Id;

            state.ReaderTask = Task.Run(() => ReadLoopAsync(state), CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                stream.Write($"cd {QuoteShellArgument(workingDirectory)}\n");
                stream.Flush();
            }

            logger.LogInformation("Started SSH terminal session {SessionId} for host {HostId}", session.Id, host.Id);
            logger.LogInformation(
                "SSH terminal setup for host {HostId}: credentials {CredentialsMs} ms, SSH connect {ConnectMs} ms, working directory {DirectoryMs} ms, shell {ShellMs} ms",
                host.Id,
                Stopwatch.GetElapsedTime(setupStarted, credentialsResolved).TotalMilliseconds,
                Stopwatch.GetElapsedTime(credentialsResolved, sshConnected).TotalMilliseconds,
                Stopwatch.GetElapsedTime(sshConnected, workingDirectoryResolved).TotalMilliseconds,
                Stopwatch.GetElapsedTime(workingDirectoryResolved, shellOpened).TotalMilliseconds);

            return session;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && setupCancellation.IsCancellationRequested)
        {
            Report($"Timed out during {setupStage} after {Stopwatch.GetElapsedTime(setupStarted).TotalSeconds:0.0} s");
            logger.LogWarning(
                "SSH terminal setup timed out for host {HostId} as {Username} during {Stage} after {ElapsedMs} ms",
                host.Id,
                credentials.Username,
                setupStage,
                Stopwatch.GetElapsedTime(setupStarted).TotalMilliseconds);
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

            throw new TimeoutException(
                $"The terminal session did not finish opening during {setupStage} within {SessionSetupTimeout.TotalSeconds:0} seconds.",
                exception);
        }
        catch (Exception exception)
        {
            Report($"Failed during {setupStage}: {exception.Message}");
            if (setupStage == "shell creation")
            {
                Report($"SSH failure type: {exception.GetType().FullName}");
                foreach (var frame in (exception.StackTrace ?? string.Empty)
                             .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .Where(static frame => frame.Contains("Renci.SshNet", StringComparison.Ordinal))
                             .Take(8))
                {
                    Report(frame.Split(" in ", 2, StringSplitOptions.None)[0]);
                }
            }
            logger.LogWarning(
                exception,
                "SSH terminal setup failed for host {HostId} as {Username} during {Stage} after {ElapsedMs} ms",
                host.Id,
                credentials.Username,
                setupStage,
                Stopwatch.GetElapsedTime(setupStarted).TotalMilliseconds);
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
                state.Session.LastActivityUtc)
            {
                Username = state.Credentials.Username
            };

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
        if (request.StandardInput is { IsSensitive: true } && !request.AuthenticateSudo)
            throw new InvalidOperationException("Sensitive terminal input requires a trusted authentication operation.");
        if (request.AuthenticateSudo && request.StandardInput is { IsSensitive: false })
            throw new InvalidOperationException("Private sudo authentication requires secure input.");
        if (request.AuthenticateSudo) _ = TerminalSudoAuthentication.BuildCommand(request.CommandText);
        // Ordinary follow-up commands must use the same session authentication as
        // the password submission. Each SSH exec has its own sudo timestamp context.
        var authenticateSudo = request.AuthenticateSudo ||
            (request.StandardInput is null && TerminalSudoAuthentication.TryGetOperation(request.CommandText, out _));

        await state.AiCommandGate.WaitAsync(cancellationToken);
        try
        {
            EnsureSessionIsActive(state);
            var commandInput = request.StandardInput;
            if (authenticateSudo && commandInput is null)
            {
                lock (state.SyncRoot)
                {
                    if (state.SudoInputExpiresAt > DateTimeOffset.UtcNow && state.ProtectedSudoInput is not null && dataProtectionProvider is not null)
                        commandInput = new CommandExecutionInput(dataProtectionProvider.CreateProtector("LMS.Terminal.Sudo", state.Session.Id.ToString()).Unprotect(state.ProtectedSudoInput), true);
                    else state.ProtectedSudoInput = null;
                }
            }
            var executionCommand = authenticateSudo
                ? commandInput is null
                    ? "exec </dev/null; /usr/bin/sudo -n -- " + GetSudoOperation(request.CommandText)
                    : TerminalSudoAuthentication.BuildCommand(request.CommandText)
                : request.CommandText;
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
            var hostIdentityChanged = false;
            client.HostKeyReceived += (_, args) =>
            {
                args.CanTrust = state.RemoteHostKey.Length > 0 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(state.RemoteHostKey, args.HostKey);
                hostIdentityChanged = !args.CanTrust;
            };
            Task? connectTask = null;
            Task? commandTask = null;
            Task? inputTask = null;
            Task? stdoutTask = null;
            Task? stderrTask = null;

            try
            {
                connectTask = Task.Run(client.Connect, CancellationToken.None);
                await connectTask.WaitAsync(commandToken);

                using var command = client.CreateCommand(BuildAiRemoteCommand(executionCommand, workingDirectory));
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
                inputTask = commandInput is null
                    ? Task.CompletedTask
                    : WriteAiCommandInputAsync(command.CreateInputStream(), commandInput!, commandToken);
                stdoutTask = PumpAiCommandStreamAsync(
                    command.OutputStream,
                    CommandExecutionOutputChannel.StandardOutput,
                    outputBuilder,
                    progress,
                    commandInput,
                    commandToken);
                stderrTask = PumpAiCommandStreamAsync(
                    command.ExtendedOutputStream,
                    CommandExecutionOutputChannel.StandardError,
                    errorBuilder,
                    progress,
                    commandInput,
                    commandToken);
                commandTask = Task.Run(() => command.EndExecute(asyncResult), CancellationToken.None);

                await commandTask.WaitAsync(commandToken);
                await Task.WhenAll(stdoutTask, stderrTask, inputTask).WaitAsync(commandToken);

                var completedAt = DateTimeOffset.UtcNow;
                var output = RedactSensitiveInput(outputBuilder.ToString(), commandInput);
                var error = RedactSensitiveInput(errorBuilder.ToString(), commandInput);
                if (authenticateSudo && commandInput is not null)
                {
                    lock (state.SyncRoot)
                    {
                        if (errorBuilder.ToString().Split('\n').Any(line => line.TrimEnd('\r') == TerminalSudoAuthentication.SuccessMarker) &&
                            state.Session.Status == TerminalSessionStatus.Active && !commandToken.IsCancellationRequested && dataProtectionProvider is not null)
                        {
                            // Encrypted, in memory only, isolated to this SSH session/account.
                            state.ProtectedSudoInput = dataProtectionProvider.CreateProtector("LMS.Terminal.Sudo", state.Session.Id.ToString()).Protect(commandInput.Content);
                            if (request.StandardInput is not null) state.SudoInputExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
                        }
                        else if (error.Contains(TerminalSudoAuthentication.FailureMarker, StringComparison.Ordinal)) state.ProtectedSudoInput = null;
                    }
                    error = error.Replace(TerminalSudoAuthentication.SuccessMarker, string.Empty, StringComparison.Ordinal).Trim();
                }
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
                    completedAt) { AdministratorAuthenticated = errorBuilder.ToString().Split('\n').Any(line => line.TrimEnd('\r') == TerminalSudoAuthentication.SuccessMarker) };
            }
            catch (Exception exception) when (hostIdentityChanged)
            {
                lock (state.SyncRoot) state.ProtectedSudoInput = null;
                throw new InvalidOperationException("The host's SSH identity changed since this terminal connected. No password was sent. Reconnect and verify the destination before continuing.", exception);
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

    private static string GetSudoOperation(string command)
    {
        if (!TerminalSudoAuthentication.TryGetOperation(command, out var operation)) throw new InvalidOperationException("Unsupported administrator operation.");
        return operation;
    }

    private static void AbortTransport(SessionState state)
    {
        lock (state.SyncRoot) state.ProtectedSudoInput = null;
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

        var closeStarted = Stopwatch.GetTimestamp();
        logger.LogInformation("Closing SSH terminal session {SessionId} for host {HostId} as {Username}",
            sessionId, state.Host.Id, state.Credentials.Username);

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
        logger.LogInformation("SSH terminal transport closed for session {SessionId} after {ElapsedMs} ms",
            sessionId, Stopwatch.GetElapsedTime(closeStarted).TotalMilliseconds);
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

        logger.LogInformation("SSH terminal session {SessionId} close completed after {ElapsedMs} ms",
            sessionId, Stopwatch.GetElapsedTime(closeStarted).TotalMilliseconds);
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
        var readerStarted = Stopwatch.GetTimestamp();
        var firstOutputLogged = false;

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
                if (!firstOutputLogged)
                {
                    firstOutputLogged = true;
                    logger.LogInformation("SSH terminal first output for session {SessionId} host {HostId} as {Username} after {ElapsedMs} ms",
                        state.Session.Id, state.Host.Id, state.Credentials.Username,
                        Stopwatch.GetElapsedTime(readerStarted).TotalMilliseconds);
                }

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

    private static async Task ReportDnsCandidatesAsync(
        string hostname,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            return;
        }

        if (IPAddress.TryParse(hostname, out var literalAddress))
        {
            var family = literalAddress.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";
            var loopback = IPAddress.IsLoopback(literalAddress) ? " loopback" : string.Empty;
            progress($"Configured target is a literal {family}{loopback} address: {literalAddress}");
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostname, timeout.Token);
            var candidates = addresses.Take(8).Select(address =>
                $"{address} ({(address.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4")}{(IPAddress.IsLoopback(address) ? ", loopback" : string.Empty)})");
            var suffix = addresses.Length > 8 ? $" (+{addresses.Length - 8} more)" : string.Empty;
            progress($"DNS candidates for {hostname}: {string.Join(", ", candidates)}{suffix}. These are not the confirmed SSH peer address.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            progress($"DNS diagnostic lookup for {hostname} exceeded 2 seconds; SSH continues independently.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            progress($"DNS diagnostic lookup for {hostname} failed: {exception.Message}. SSH continues independently.");
        }
    }

    internal static bool ShouldUseConnectedUserHome(ManagedHost host, TerminalConnectionRequest request, string username) =>
        !string.Equals(username, host.Username.Trim(), StringComparison.Ordinal) &&
        (string.IsNullOrWhiteSpace(request.WorkingDirectory) ||
         string.Equals(request.WorkingDirectory.Trim(), host.DefaultWorkingDirectory.Trim(), StringComparison.Ordinal));

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
            try
            {
                await stream.WriteAsync(bytes.AsMemory(), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
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
        Guid? ownerId,
        byte[] remoteHostKey)
    {
        public byte[] RemoteHostKey { get; } = remoteHostKey;
        public string? ProtectedSudoInput { get; set; }
        public DateTimeOffset SudoInputExpiresAt { get; set; }
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
