// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Core.Models.DesktopSession;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinuxMadeSane.DesktopHelper;

public sealed class DesktopSessionClientHostedService(
    DesktopSessionEnvironmentDetector detector,
    DesktopActionExecutor actionExecutor,
    DesktopAssistantLaunchTicketCache launchTicketCache,
    DesktopAssistantNativeMessageBus nativeMessageBus,
    IOptions<DesktopSessionHelperOptions> options,
    ILogger<DesktopSessionClientHostedService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            logger.LogInformation("LMS desktop helper is unavailable because Unix domain sockets are not supported.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReportAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException exception)
            {
                logger.LogWarning(
                    "LMS desktop helper could not reach broker socket {SocketPath}: {SocketErrorCode} ({NativeErrorCode}) {Message}. {SocketStatus} Retrying in {RetrySeconds}s.",
                    ResolveSocketPath(),
                    exception.SocketErrorCode,
                    exception.NativeErrorCode,
                    exception.Message,
                    DescribeSocketPath(ResolveSocketPath()),
                    RetryDelay.TotalSeconds);
                logger.LogDebug(exception, "LMS desktop helper broker socket connect failure.");
                await Task.Delay(RetryDelay, stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "LMS desktop helper could not reach broker socket {SocketPath}: {Message}. {SocketStatus} Retrying in {RetrySeconds}s.",
                    ResolveSocketPath(),
                    exception.Message,
                    DescribeSocketPath(ResolveSocketPath()),
                    RetryDelay.TotalSeconds);
                logger.LogDebug(exception, "LMS desktop helper broker connect failure.");
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }
    }

    private async Task ConnectAndReportAsync(CancellationToken cancellationToken)
    {
        var socketPath = ResolveSocketPath();
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);

        using var stream = new NetworkStream(socket, ownsSocket: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: false)
        {
            AutoFlush = true
        };
        using var writerLock = new SemaphoreSlim(1, 1);

        logger.LogInformation("LMS desktop helper connected to {SocketPath}.", socketPath);
        await SendReportAsync(
            writer,
            writerLock,
            DesktopSessionBrokerMessageTypes.Hello,
            collectDiagnostics: false,
            evidenceRequestId: null,
            cancellationToken);

        var heartbeatTask = RunHeartbeatAsync(writer, writerLock, cancellationToken);
        var requestTask = RunRequestReaderAsync(reader, writer, writerLock, cancellationToken);
        var completed = await Task.WhenAny(heartbeatTask, requestTask);
        await completed;
    }

    private async Task RunHeartbeatAsync(
        TextWriter writer,
        SemaphoreSlim writerLock,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(HeartbeatDelay, cancellationToken);
            await SendReportAsync(
                writer,
                writerLock,
                DesktopSessionBrokerMessageTypes.Heartbeat,
                collectDiagnostics: false,
                evidenceRequestId: null,
                cancellationToken);
        }
    }

    private async Task RunRequestReaderAsync(
        TextReader reader,
        TextWriter writer,
        SemaphoreSlim writerLock,
        CancellationToken cancellationToken)
    {
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
            if (message?.LaunchTicket is not null &&
                string.Equals(message.MessageType, DesktopSessionBrokerMessageTypes.LaunchTicket, StringComparison.OrdinalIgnoreCase))
            {
                launchTicketCache.Update(message.LaunchTicket);
                logger.LogDebug(
                    "LMS desktop helper received launch ticket {TokenPreview} expiring {ExpiresAtUtc}. Cache: {CacheState}.",
                    DesktopAssistantLaunchTicketCache.PreviewToken(message.LaunchTicket.Token),
                    message.LaunchTicket.ExpiresAtUtc,
                    FormatTicketCacheSnapshot(launchTicketCache.GetDebugSnapshot()));
                continue;
            }

            if (message?.Theme is not null &&
                string.Equals(message.MessageType, DesktopSessionBrokerMessageTypes.ThemeChanged, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("LMS desktop helper received Desktop Assistant theme update.");
                nativeMessageBus.PublishThemeChanged(message.Theme);
                continue;
            }

            if (message is not null &&
                string.Equals(message.MessageType, DesktopSessionBrokerMessageTypes.RefreshEvidence, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "LMS desktop helper received evidence refresh request {EvidenceRequestId}.",
                    message.EvidenceRequestId);
                await SendReportAsync(
                    writer,
                    writerLock,
                    DesktopSessionBrokerMessageTypes.EvidenceReport,
                    collectDiagnostics: true,
                    message.EvidenceRequestId,
                    cancellationToken);
                continue;
            }

            if (message?.ActionRequest is null ||
                !string.Equals(message.MessageType, DesktopSessionBrokerMessageTypes.ActionRequest, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            logger.LogInformation(
                "LMS desktop helper received action request {RequestId} ({ActionType}).",
                message.ActionRequest.RequestId,
                message.ActionRequest.ActionKind);
            var result = await actionExecutor.ExecuteAsync(message.ActionRequest, cancellationToken);
            await SendActionResultAsync(writer, writerLock, result, cancellationToken);
            await SendReportAsync(
                writer,
                writerLock,
                DesktopSessionBrokerMessageTypes.Heartbeat,
                collectDiagnostics: true,
                evidenceRequestId: null,
                cancellationToken);
        }
    }

    private async Task SendReportAsync(
        TextWriter writer,
        SemaphoreSlim writerLock,
        string messageType,
        bool collectDiagnostics,
        Guid? evidenceRequestId,
        CancellationToken cancellationToken)
    {
        var report = detector.DetectCurrent();
        if (collectDiagnostics)
        {
            report = report with
            {
                ReadOnlyDiagnostics = await DesktopReadOnlyDiagnosticsCollector.CollectAsync(report, cancellationToken)
            };
        }

        if (string.Equals(messageType, DesktopSessionBrokerMessageTypes.Heartbeat, StringComparison.OrdinalIgnoreCase) &&
            !collectDiagnostics)
        {
            logger.LogDebug(
                "LMS desktop helper sending heartbeat report (user={UserName}, uid={UserId}, displayServer={DisplayServer}, display={Display}, warnings={WarningCount}).",
                report.UserName,
                report.UserId,
                report.DisplayServer,
                report.Display ?? "none",
                report.Warnings.Count);
        }
        else
        {
            logger.LogInformation(
                "LMS desktop helper sending {MessageType} report (diagnostics={CollectDiagnostics}, user={UserName}, uid={UserId}, displayServer={DisplayServer}, display={Display}, wayland={WaylandDisplay}, runtime={RuntimeDirectory}, hasDisplay={HasDisplay}, hasBus={HasSessionBus}, canLaunchGui={CanLaunchGuiApps}, warnings={WarningCount}).",
                messageType,
                collectDiagnostics,
                report.UserName,
                report.UserId,
                report.DisplayServer,
                report.Display ?? "none",
                report.WaylandDisplay ?? "none",
                report.XdgRuntimeDirectory ?? "none",
                report.HasDisplay,
                report.HasSessionBus,
                report.CanLaunchGuiApps,
                report.Warnings.Count);
        }

        var message = new DesktopSessionBrokerMessage(
            messageType,
            report,
            DateTimeOffset.UtcNow)
        {
            EvidenceRequestId = evidenceRequestId
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

    private static async Task SendActionResultAsync(
        TextWriter writer,
        SemaphoreSlim writerLock,
        DesktopSessionActionResult result,
        CancellationToken cancellationToken)
    {
        var message = new DesktopSessionBrokerMessage(
            DesktopSessionBrokerMessageTypes.ActionResult,
            null,
            DateTimeOffset.UtcNow)
        {
            ActionResult = result
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

    private string ResolveSocketPath()
    {
        var environmentValue = Environment.GetEnvironmentVariable("LMS_DESKTOP_HELPER_SOCKET");
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue.Trim();
        }

        return string.IsNullOrWhiteSpace(options.Value.SocketPath)
            ? "/run/linuxmadesane/desktop-session.sock"
            : options.Value.SocketPath.Trim();
    }

    private TimeSpan HeartbeatDelay => TimeSpan.FromSeconds(Math.Max(5, options.Value.HeartbeatSeconds));

    private TimeSpan RetryDelay => TimeSpan.FromSeconds(Math.Max(3, options.Value.RetrySeconds));

    private static string FormatTicketCacheSnapshot(DesktopAssistantLaunchTicketDebugSnapshot snapshot) =>
        $"hasTicket={snapshot.HasTicket}, valid={snapshot.HasValidTicket}, token={snapshot.TokenPreview}, expires={snapshot.ExpiresAtUtc?.ToString("O") ?? "none"}, waiter={snapshot.WaiterActive}";

    private static string DescribeSocketPath(string socketPath)
    {
        var directory = Path.GetDirectoryName(socketPath);
        var fileName = Path.GetFileName(socketPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return "socketStatus=invalid-path";
        }

        try
        {
            var directoryExists = Directory.Exists(directory);
            var entryExists = directoryExists &&
                              Directory.EnumerateFileSystemEntries(directory, fileName).Any();
            var mode = "unknown";
            if (entryExists && OperatingSystem.IsLinux())
            {
                try
                {
                    mode = File.GetUnixFileMode(socketPath).ToString();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    mode = exception.GetType().Name;
                }
            }

            return $"socketStatus=dirExists:{directoryExists}, entryExists:{entryExists}, mode:{mode}, user:{Environment.UserName}, pathLength:{socketPath.Length}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"socketStatus=unreadable:{exception.GetType().Name}, user:{Environment.UserName}, pathLength:{socketPath.Length}";
        }
    }
}
