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

        logger.LogInformation(
            "Desktop session broker is listening on {SocketPath}. Launch ticket issuer: {LaunchTicketIssuerState}.",
            socketPath,
            launchTicketIssuer is null ? "missing" : "ready");

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
        logger.LogInformation("Desktop helper broker connection {ConnectionId} accepted.", connectionId);
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
            broker.RegisterNotificationSender(
                connectionId,
                (message, token) => SendMessageAsync(writer, writerLock, message, token));

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
                    logger.LogWarning(
                        "Desktop helper broker connection {ConnectionId} sent unsupported message type {MessageType}.",
                        connectionId,
                        message?.MessageType ?? "null");
                    continue;
                }

                if (string.Equals(message.MessageType, DesktopSessionBrokerMessageTypes.ActionResult, StringComparison.OrdinalIgnoreCase))
                {
                    if (message.ActionResult is not null)
                    {
                        logger.LogInformation(
                            "Desktop helper broker connection {ConnectionId} returned action result {RequestId} ({ActionKind}): success={Succeeded}, summary={Summary}.",
                            connectionId,
                            message.ActionResult.RequestId,
                            message.ActionResult.ActionKind,
                            message.ActionResult.Succeeded,
                            message.ActionResult.Summary);
                        await broker.CompleteActionAsync(connectionId, message.ActionResult, cancellationToken);
                    }

                    continue;
                }

                if (message.CapabilityReport is not null)
                {
                    if (string.Equals(message.MessageType, DesktopSessionBrokerMessageTypes.Heartbeat, StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogDebug(
                            "Desktop helper broker connection {ConnectionId} sent heartbeat: {ReportSummary}.",
                            connectionId,
                            FormatCapabilityReport(message.CapabilityReport));
                    }
                    else
                    {
                        logger.LogInformation(
                            "Desktop helper broker connection {ConnectionId} sent {MessageType}: {ReportSummary}.",
                            connectionId,
                            message.MessageType,
                            FormatCapabilityReport(message.CapabilityReport));
                    }

                    await broker.RegisterOrRefreshAsync(
                        connectionId,
                        message.CapabilityReport,
                        cancellationToken,
                        preserveReadOnlyDiagnosticsWhenEmpty: string.Equals(
                            message.MessageType,
                            DesktopSessionBrokerMessageTypes.Heartbeat,
                            StringComparison.OrdinalIgnoreCase));
                    if (string.Equals(message.MessageType, DesktopSessionBrokerMessageTypes.EvidenceReport, StringComparison.OrdinalIgnoreCase) &&
                        message.EvidenceRequestId is { } evidenceRequestId)
                    {
                        await broker.CompleteEvidenceRefreshAsync(
                            connectionId,
                            evidenceRequestId,
                            message.CapabilityReport,
                            cancellationToken);
                    }

                    if (launchTicketIssuer is not null)
                    {
                        var ticket = launchTicketIssuer.Issue("/desktop-assistant?fromTray=1");
                        logger.LogDebug(
                            "Desktop helper broker connection {ConnectionId} issuing launch ticket {TokenPreview} expiring {ExpiresAtUtc}.",
                            connectionId,
                            PreviewToken(ticket.Token),
                            ticket.ExpiresAtUtc);
                        await SendLaunchTicketAsync(
                            writer,
                            writerLock,
                            ticket,
                            cancellationToken);
                    }
                    else
                    {
                        logger.LogWarning(
                            "Desktop helper broker connection {ConnectionId} cannot receive launch tickets because no issuer is registered.",
                            connectionId);
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
            logger.LogDebug(exception, "Desktop helper broker connection {ConnectionId} disconnected.", connectionId);
        }
        catch (SocketException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Desktop helper broker connection {ConnectionId} socket disconnected.", connectionId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
        finally
        {
            writerLock.Dispose();
            broker.MarkDisconnected(connectionId);
            logger.LogInformation("Desktop helper broker connection {ConnectionId} marked disconnected.", connectionId);
        }
    }

    private static bool IsSupportedMessageType(string messageType) =>
        string.Equals(messageType, DesktopSessionBrokerMessageTypes.Hello, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(messageType, DesktopSessionBrokerMessageTypes.Heartbeat, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(messageType, DesktopSessionBrokerMessageTypes.EvidenceReport, StringComparison.OrdinalIgnoreCase) ||
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
        await SendMessageAsync(writer, writerLock, message, cancellationToken);
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
        await SendMessageAsync(writer, writerLock, message, cancellationToken);
    }

    private static async Task SendMessageAsync(
        TextWriter writer,
        SemaphoreSlim writerLock,
        DesktopSessionBrokerMessage message,
        CancellationToken cancellationToken)
    {
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

    private static string FormatCapabilityReport(DesktopSessionCapabilityReport report) =>
        $"user={report.UserName}, uid={report.UserId?.ToString() ?? "none"}, machine={report.MachineName}, pid={report.ProcessId}, displayServer={report.DisplayServer}, display={report.Display ?? "none"}, wayland={report.WaylandDisplay ?? "none"}, runtime={report.XdgRuntimeDirectory ?? "none"}, sessionType={report.SessionType ?? "none"}, sessionClass={report.SessionClass ?? "none"}, desktop={report.CurrentDesktop ?? report.DesktopSession ?? "none"}, hasDisplay={report.HasDisplay}, hasBus={report.HasSessionBus}, canLaunchGui={report.CanLaunchGuiApps}, warnings={report.Warnings.Count}";

    private static string PreviewToken(string token) =>
        string.IsNullOrWhiteSpace(token)
            ? "none"
            : token.Length <= 8
                ? token
                : $"{token[..8]}...";

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
