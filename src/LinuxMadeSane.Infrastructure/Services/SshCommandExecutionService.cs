// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using Microsoft.Extensions.Logging;
using System.Text;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SshCommandExecutionService(
    ManagedHostSshConnectionFactory sshConnectionFactory,
    ILogger<SshCommandExecutionService> logger) : ICommandExecutionService
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    public async Task<CommandExecutionResult> ExecuteAsync(
        ManagedHost host,
        string commandText,
        IProgress<CommandExecutionUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(commandText))
        {
            throw new InvalidOperationException("A command is required.");
        }

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

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    command.CancelAsync();
                }
                catch
                {
                    // Ignore cancellation cleanup failures.
                }
            });

            var asyncResult = command.BeginExecute();
            var stdoutTask = PumpStreamAsync(
                command.OutputStream,
                CommandExecutionOutputChannel.StandardOutput,
                outputBuilder,
                progress,
                cancellationToken);
            var stderrTask = PumpStreamAsync(
                command.ExtendedOutputStream,
                CommandExecutionOutputChannel.StandardError,
                errorBuilder,
                progress,
                cancellationToken);

            await Task.Run(() => command.EndExecute(asyncResult), cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask);

            var output = outputBuilder.ToString();
            var error = errorBuilder.ToString();
            var exitStatus = command.ExitStatus;
            var completedAt = DateTimeOffset.UtcNow;

            progress?.Report(new CommandExecutionCompletedUpdate(exitStatus ?? -1, completedAt));

            return new CommandExecutionResult(
                commandText,
                exitStatus ?? -1,
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

    private static async Task PumpStreamAsync(
        Stream stream,
        CommandExecutionOutputChannel channel,
        StringBuilder builder,
        IProgress<CommandExecutionUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead <= 0)
            {
                break;
            }

            var chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            if (string.IsNullOrEmpty(chunk))
            {
                continue;
            }

            builder.Append(chunk);
            progress?.Report(new CommandExecutionOutputUpdate(
                channel,
                chunk,
                false,
                DateTimeOffset.UtcNow));
        }
    }
}
