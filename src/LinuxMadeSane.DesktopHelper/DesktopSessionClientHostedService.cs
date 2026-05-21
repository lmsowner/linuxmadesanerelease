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
            catch (Exception exception)
            {
                logger.LogDebug(exception, "LMS desktop helper could not reach the broker.");
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
        await SendReportAsync(writer, writerLock, DesktopSessionBrokerMessageTypes.Hello, cancellationToken);

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
            await SendReportAsync(writer, writerLock, DesktopSessionBrokerMessageTypes.Heartbeat, cancellationToken);
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
                continue;
            }

            if (message?.ActionRequest is null ||
                !string.Equals(message.MessageType, DesktopSessionBrokerMessageTypes.ActionRequest, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var result = await actionExecutor.ExecuteAsync(message.ActionRequest, cancellationToken);
            await SendActionResultAsync(writer, writerLock, result, cancellationToken);
            await SendReportAsync(writer, writerLock, DesktopSessionBrokerMessageTypes.Heartbeat, cancellationToken);
        }
    }

    private async Task SendReportAsync(
        TextWriter writer,
        SemaphoreSlim writerLock,
        string messageType,
        CancellationToken cancellationToken)
    {
        var report = detector.DetectCurrent();
        report = report with
        {
            ReadOnlyDiagnostics = await DesktopReadOnlyDiagnosticsCollector.CollectAsync(report, cancellationToken)
        };
        var message = new DesktopSessionBrokerMessage(
            messageType,
            report,
            DateTimeOffset.UtcNow);
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
}
