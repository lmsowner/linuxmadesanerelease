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
    ILogger<ManagedHostCommandExecutionService> logger) : ICommandExecutionService
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

        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            return await ExecuteLocalAsync(host, commandText, progress, cancellationToken);
        }

        return await ExecuteRemoteAsync(host, commandText, progress, cancellationToken);
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
