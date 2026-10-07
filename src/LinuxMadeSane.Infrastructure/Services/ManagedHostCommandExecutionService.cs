// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

// Guardrail: host-aware command execution lives here. Callers should not branch on
// local-vs-remote execution themselves, because that duplicates transport behavior.
public sealed class ManagedHostCommandExecutionService(
    ManagedHostSshConnectionFactory sshConnectionFactory,
    ILinuxCommandRunner linuxCommandRunner,
    ILogger<ManagedHostCommandExecutionService> logger) : ICommandExecutionService, ICommandExecutionInputService, IManagedHostPublicKeyInstaller
{
    public async Task<CommandExecutionResult> InstallAsync(ManagedHost host, string publicKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(publicKey)) throw new InvalidOperationException("A public key is required.");
        var command = SshAuthorizedKeyInstallCommandBuilder.Build(publicKey);
        if (!AiLocalMachine.IsLocalMachine(host.Id))
            return await ExecuteRemoteAsync(host, command, null, null, cancellationToken);

        if (string.IsNullOrWhiteSpace(host.Username) || host.Username.StartsWith('-') || host.Username.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("Select the local Linux account for this key.");
        // The service cannot enter another user's private home. Start in / and
        // let sudo select the target account and its real home from passwd.
        // This is a typed key-install operation, not elevation of arbitrary chat commands.
        var result = await linuxCommandRunner.RunAsync(new LinuxCommandRequest(
            "sudo", ["-n", "-H", "-u", host.Username, "--", "/bin/sh", "-c", command],
            true, TimeSpan.FromSeconds(30), $"Install SSH public key for local account {host.Username}.", "/"), false, cancellationToken);
        var error = result.ExitCode == 0 ? result.StandardError :
            $"LMS could not install the key for local account '{host.Username}' using its service sudo access. Your existing login is unchanged. " + result.StandardError;
        return new(command, result.ExitCode, result.StandardOutput, error, result.StartedAt, result.CompletedAt);
    }

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    public Task<CommandExecutionResult> ExecuteAsync(
        ManagedHost host,
        string commandText,
        IProgress<CommandExecutionUpdate>? progress = null,
        CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(host, commandText, null, progress, cancellationToken);

    public async Task<CommandExecutionResult> ExecuteAsync(
        ManagedHost host,
        string commandText,
        CommandExecutionInput input,
        IProgress<CommandExecutionUpdate>? progress = null,
        CancellationToken cancellationToken = default)
        => await ExecuteCoreAsync(host, commandText, input, progress, cancellationToken);

    private async Task<CommandExecutionResult> ExecuteCoreAsync(
        ManagedHost host,
        string commandText,
        CommandExecutionInput? input,
        IProgress<CommandExecutionUpdate>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(commandText))
        {
            throw new InvalidOperationException("A command is required.");
        }

        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            if (input is not null)
            {
                throw new InvalidOperationException("Command input is only supported for SSH host execution.");
            }

            return await ExecuteLocalAsync(host, commandText, progress, cancellationToken);
        }

        return await ExecuteRemoteAsync(host, commandText, input, progress, cancellationToken);
    }

    private async Task<CommandExecutionResult> ExecuteLocalAsync(
        ManagedHost host,
        string commandText,
        IProgress<CommandExecutionUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        progress?.Report(new CommandExecutionStartedUpdate(commandText, startedAt));

        var result = await linuxCommandRunner.RunAsync(
            new LinuxCommandRequest(
                "/bin/sh",
                ["-lc", commandText],
                false,
                TimeSpan.FromMinutes(30),
                $"Execute command on {host.Name}.",
                host.DefaultWorkingDirectory),
            dryRun: false,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            progress?.Report(new CommandExecutionOutputUpdate(
                CommandExecutionOutputChannel.StandardOutput,
                result.StandardOutput,
                true,
                result.CompletedAt));
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            progress?.Report(new CommandExecutionOutputUpdate(
                CommandExecutionOutputChannel.StandardError,
                result.StandardError,
                true,
                result.CompletedAt));
        }

        progress?.Report(new CommandExecutionCompletedUpdate(result.ExitCode, result.CompletedAt));

        return new CommandExecutionResult(
            commandText,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            result.StartedAt,
            result.CompletedAt);
    }

    private async Task<CommandExecutionResult> ExecuteRemoteAsync(
        ManagedHost host,
        string commandText,
        CommandExecutionInput? input,
        IProgress<CommandExecutionUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var credentials = await sshConnectionFactory.ResolveStoredCredentialsAsync(host, cancellationToken);

        var startedAt = DateTimeOffset.UtcNow;
        progress?.Report(new CommandExecutionStartedUpdate(commandText, startedAt));
        using var client = sshConnectionFactory.CreateSshClient(host, credentials, ConnectTimeout, KeepAliveInterval);

        try
        {
            logger.LogInformation("Executing SSH command for host {HostId}: {CommandText}", host.Id, commandText);

            client.Connect();

            using var command = client.CreateCommand(commandText);
            command.CommandTimeout = TimeSpan.FromMinutes(30);

            var (output, error, exitStatus) = await ExecuteSshCommandAsync(command, input, progress, cancellationToken);
            var completedAt = DateTimeOffset.UtcNow;

            progress?.Report(new CommandExecutionCompletedUpdate(exitStatus, completedAt));

            return new CommandExecutionResult(
                commandText,
                exitStatus,
                output ?? string.Empty,
                error,
                startedAt,
                completedAt);
        }
        finally
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
    }

    internal static async Task<(string Output, string Error, int ExitCode)> ExecuteSshCommandAsync(
        Renci.SshNet.SshCommand command, CommandExecutionInput? input,
        IProgress<CommandExecutionUpdate>? progress, CancellationToken token)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();
        // EndExecute reads command.Result, competing with our stdout pump and
        // consuming the bytes after its first 4096-byte buffer. ExecuteAsync only
        // waits for completion, leaving both streams exclusively to these readers.
        var execution = command.ExecuteAsync(token);
        var stdin = input is null ? Task.CompletedTask : WriteInputAsync(command.CreateInputStream(), input, token);
        await Task.WhenAll(execution,
            PumpStreamAsync(command.OutputStream, CommandExecutionOutputChannel.StandardOutput, output, progress, token),
            PumpStreamAsync(command.ExtendedOutputStream, CommandExecutionOutputChannel.StandardError, error, progress, token), stdin);
        return (output.ToString(), error.ToString(), command.ExitStatus ?? -1);
    }

    internal static async Task PumpStreamAsync(
        Stream stream, CommandExecutionOutputChannel channel, StringBuilder builder,
        IProgress<CommandExecutionUpdate>? progress, CancellationToken token)
    {
        // A decoder must retain partial UTF-8 characters across stream buffers.
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token);
            if (count == 0) break;
            var chunk = new string(buffer, 0, count);
            builder.Append(chunk);
            progress?.Report(new CommandExecutionOutputUpdate(channel, chunk, false, DateTimeOffset.UtcNow));
        }
    }

    private static async Task WriteInputAsync(
        Stream stream,
        CommandExecutionInput input,
        CancellationToken cancellationToken)
    {
        using (stream)
        {
            var bytes = Encoding.UTF8.GetBytes(input.Content);
            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }
}
