// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Threading.Channels;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

// AI command execution is deliberately kept off the interactive terminal stream.
// This daemon is an in-process, bounded command channel: it exposes no TCP, HTTP,
// named-pipe, or Unix-socket listener that another machine or local process can call.
public sealed class TerminalAiCommandDaemon(
    ITerminalSessionService terminalSessionService,
    ILogger<TerminalAiCommandDaemon> logger) : BackgroundService, ITerminalAiCommandDaemon
{
    private const int WorkerCount = 4;
    private readonly Channel<CommandWorkItem> queue = Channel.CreateBounded<CommandWorkItem>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    public async Task<TerminalAiCommandResult> ExecuteAsync(
        TerminalAiCommandRequest request,
        IProgress<CommandExecutionUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.RequestId == Guid.Empty || request.TerminalSessionId == Guid.Empty)
        {
            throw new InvalidOperationException("A valid AI command and terminal session id are required.");
        }

        if (string.IsNullOrWhiteSpace(request.CommandText))
        {
            throw new InvalidOperationException("The AI command was empty.");
        }

        var completion = new TaskCompletionSource<TerminalAiCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(
            new CommandWorkItem(request, progress, completion, cancellationToken),
            cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, WorkerCount)
            .Select(_ => RunWorkerAsync(stoppingToken));
        return Task.WhenAll(workers);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ExecuteWorkItemAsync(item, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ExecuteWorkItemAsync(CommandWorkItem item, CancellationToken stoppingToken)
    {
        if (item.CancellationToken.IsCancellationRequested)
        {
            item.Completion.TrySetCanceled(item.CancellationToken);
            return;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            item.CancellationToken);
        try
        {
            var result = await terminalSessionService.ExecuteAiCommandAsync(
                item.Request,
                item.Progress,
                linkedCancellation.Token);
            item.Completion.TrySetResult(result);
            logger.LogInformation(
                "Terminal AI command {RequestId} completed for session {SessionId} with exit code {ExitCode}.",
                item.Request.RequestId,
                item.Request.TerminalSessionId,
                result.ExitCode);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            item.Completion.TrySetCanceled(item.CancellationToken.IsCancellationRequested
                ? item.CancellationToken
                : stoppingToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Terminal AI command {RequestId} failed for session {SessionId}: {FailureType}.",
                item.Request.RequestId,
                item.Request.TerminalSessionId,
                exception.GetType().Name);
            item.Completion.TrySetException(exception);
        }
    }

    private sealed record CommandWorkItem(
        TerminalAiCommandRequest Request,
        IProgress<CommandExecutionUpdate>? Progress,
        TaskCompletionSource<TerminalAiCommandResult> Completion,
        CancellationToken CancellationToken);
}
