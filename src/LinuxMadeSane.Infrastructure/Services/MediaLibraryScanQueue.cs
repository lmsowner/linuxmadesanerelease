// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Threading.Channels;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class MediaLibraryScanQueue(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<MediaLibraryScanQueue> logger) : BackgroundService, IMediaLibraryScanQueue
{
    private readonly Channel<Guid?> queue = Channel.CreateUnbounded<Guid?>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask EnqueueScanAsync(Guid? rootId, CancellationToken cancellationToken = default) =>
        queue.Writer.WriteAsync(rootId, cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var rootId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var dataService = scope.ServiceProvider.GetRequiredService<IMediaLibraryIntegrationDataService>();
                var result = await dataService.ScanAsync(rootId, stoppingToken);
                logger.LogInformation(
                    "Media library scan completed with status {Status}: {Message}",
                    result.Status,
                    result.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Media library scan failed.");
            }
        }
    }
}
