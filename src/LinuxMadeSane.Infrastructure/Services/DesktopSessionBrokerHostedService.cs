// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.DesktopSession;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class DesktopSessionBrokerHostedService(
    IDesktopSessionBroker broker,
    IOptions<DesktopSessionBrokerOptions> options,
    ILogger<DesktopSessionBrokerHostedService> logger,
    IEnumerable<IDesktopAssistantLaunchTicketIssuer> launchTicketIssuers) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDesktopAssistantLaunchTicketIssuer? launchTicketIssuer = launchTicketIssuers.FirstOrDefault();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Desktop session broker is disabled.");
            return;
        }

        if (!Socket.OSSupportsUnixDomainSockets)
        {
            logger.LogInformation("Desktop session broker is unavailable because Unix domain sockets are not supported.");
            return;
        }

        var socketPath = NormalizeSocketPath(options.Value.SocketPath);
        var socketDirectory = Path.GetDirectoryName(socketPath);
        if (string.IsNullOrWhiteSpace(socketDirectory) || !TryPrepareSocketDirectory(socketDirectory))
        {
            logger.LogWarning("Desktop session broker could not prepare socket directory for {SocketPath}.", socketPath);
            return;
        }

        TryDeleteStaleSocket(socketPath);

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(Math.Max(1, options.Value.Backlog));
            TrySetSocketMode(socketPath);
        }
        catch (Exception exception) when (exception is SocketException or UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(exception, "Desktop session broker could not bind {SocketPath}.", socketPath);
            return;
        }

        logger.LogInformation("Desktop session broker is listening on {SocketPath}.", socketPath);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(stoppingToken);
                _ = Task.Run(() => HandleConnectionAsync(socket, stoppingToken), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
        finally
        {
            TryDeleteStaleSocket(socketPath);
        }
    }

    private async Task HandleConnectionAsync(Socket socket, CancellationToken cancellationToken)
    {
        var connectionId = Guid.NewGuid().ToString("N");
        var writerLock = new SemaphoreSlim(1, 1);
        try
        {
            using var stream = new NetworkStream(socket, ownsSocket: true);
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            broker.RegisterActionSender(
                connectionId,
                (request, token) => SendActionRequestAsync(writer, writerLock, request, token));

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var message = JsonSerializer.Deserialize<DesktopSessionBrokerMessage>(line, JsonOptions);
                if (message is null || !IsSupportedMessageType(message.MessageType))
                {
                    continue;
                }

                if (string.Equals(message.MessageType, DesktopSessionBrokerMessageTypes.ActionResult, StringComparison.OrdinalIgnoreCase))
                {
                    if (message.ActionResult is not null)
                    {
                        await broker.CompleteActionAsync(connectionId, message.ActionResult, cancellationToken);
                    }

                    continue;
                }

                if (message.CapabilityReport is not null)
                {
                    await broker.RegisterOrRefreshAsync(connectionId, message.CapabilityReport, cancellationToken);
                    if (launchTicketIssuer is not null)
                    {
                        await SendLaunchTicketAsync(
                            writer,
                            writerLock,
                            launchTicketIssuer.Issue("/desktop-assistant?fromTray=1"),
                            cancellationToken);
                    }
                }
            }
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Desktop helper sent an invalid broker message.");
        }
        catch (IOException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Desktop helper disconnected.");
        }
        catch (SocketException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Desktop helper socket disconnected.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
        finally
        {
            writerLock.Dispose();
            broker.MarkDisconnected(connectionId);
        }
    }

    private static bool IsSupportedMessageType(string messageType) =>
        string.Equals(messageType, DesktopSessionBrokerMessageTypes.Hello, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(messageType, DesktopSessionBrokerMessageTypes.Heartbeat, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(messageType, DesktopSessionBrokerMessageTypes.ActionResult, StringComparison.OrdinalIgnoreCase);

    private static async Task SendActionRequestAsync(
        TextWriter writer,
        SemaphoreSlim writerLock,
        DesktopSessionActionRequest request,
        CancellationToken cancellationToken)
    {
        var message = new DesktopSessionBrokerMessage(
            DesktopSessionBrokerMessageTypes.ActionRequest,
            null,
            DateTimeOffset.UtcNow)
        {
            ActionRequest = request
        };
        var json = JsonSerializer.Serialize(message, JsonOptions);

        await writerLock.WaitAsync(cancellationToken);
        try
        {
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        }
        finally
        {
            writerLock.Release();
        }
    }

    private static async Task SendLaunchTicketAsync(
        TextWriter writer,
        SemaphoreSlim writerLock,
        DesktopAssistantLaunchTicket ticket,
        CancellationToken cancellationToken)
    {
        var message = new DesktopSessionBrokerMessage(
            DesktopSessionBrokerMessageTypes.LaunchTicket,
            null,
            DateTimeOffset.UtcNow)
        {
            LaunchTicket = ticket
        };
        var json = JsonSerializer.Serialize(message, JsonOptions);

        await writerLock.WaitAsync(cancellationToken);
        try
        {
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        }
        finally
        {
            writerLock.Release();
        }
    }

    private static string NormalizeSocketPath(string configuredPath) =>
        string.IsNullOrWhiteSpace(configuredPath)
            ? "/run/linuxmadesane/desktop-session.sock"
            : configuredPath.Trim();

    private bool TryPrepareSocketDirectory(string directoryPath)
    {
        try
        {
            Directory.CreateDirectory(directoryPath);
            TrySetDirectoryMode(directoryPath);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(exception, "Could not create desktop session broker socket directory {DirectoryPath}.", directoryPath);
            return false;
        }
    }

    private void TrySetDirectoryMode(string directoryPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                directoryPath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupRead |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead |
                UnixFileMode.OtherExecute);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            logger.LogDebug(exception, "Could not set desktop session broker directory mode.");
        }
    }

    private void TrySetSocketMode(string socketPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            // The helper socket accepts passive reports plus allowlisted action requests that
            // the web UI must approve before dispatching.
            File.SetUnixFileMode(
                socketPath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.GroupRead |
                UnixFileMode.GroupWrite);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            logger.LogDebug(exception, "Could not set desktop session broker socket mode.");
        }
    }

    private void TryDeleteStaleSocket(string socketPath)
    {
        try
        {
            File.Delete(socketPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            logger.LogDebug(exception, "Could not delete stale desktop session broker socket.");
        }
    }
}
