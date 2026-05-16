// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Collections.Concurrent;
using System.Threading.Channels;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class AiChatRunQueue(IServiceScopeFactory serviceScopeFactory) : BackgroundService, IAiChatRunQueue
{
    private readonly Channel<Guid> runQueue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> activeRunTokens = [];

    public ValueTask EnqueueAsync(Guid runId, CancellationToken cancellationToken = default) =>
        runQueue.Writer.WriteAsync(runId, cancellationToken);

    public void Cancel(Guid runId)
    {
        if (activeRunTokens.TryGetValue(runId, out var cancellationTokenSource))
        {
            cancellationTokenSource.Cancel();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueIncompleteRunsAsync(stoppingToken);

        await foreach (var runId in runQueue.Reader.ReadAllAsync(stoppingToken))
        {
            using var linkedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            activeRunTokens[runId] = linkedCancellationSource;

            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var orchestrationService = scope.ServiceProvider.GetRequiredService<IAiChatOrchestrationService>();
                await orchestrationService.ProcessRunAsync(runId, linkedCancellationSource.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || linkedCancellationSource.IsCancellationRequested)
            {
            }
            finally
            {
                activeRunTokens.TryRemove(runId, out _);
            }
        }
    }

    private async Task RequeueIncompleteRunsAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var conversationStore = scope.ServiceProvider.GetRequiredService<IAiConversationStore>();
        var runs = await conversationStore.ListChatRunsAsync(cancellationToken: cancellationToken);

        foreach (var run in runs.Where(run => !run.IsTerminal).OrderBy(run => run.CreatedAtUtc))
        {
            await runQueue.Writer.WriteAsync(run.Id, cancellationToken);
        }
    }
}
