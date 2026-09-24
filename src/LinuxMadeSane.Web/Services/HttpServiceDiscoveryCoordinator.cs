// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;
using System.Threading.Channels;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Cloudflare;
using LinuxMadeSane.Infrastructure.Services;

namespace LinuxMadeSane.Web.Services;

public sealed class HttpServiceDiscoveryCoordinator(
    IServiceScopeFactory scopeFactory,
    HttpServiceDiscoveryStorageSettings storageSettings,
    TimeProvider timeProvider,
    ILogger<HttpServiceDiscoveryCoordinator> logger) : BackgroundService
{
    private static readonly HashSet<int> AllowedRefreshIntervals = [0, 60, 180, 360, 720, 1440];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly Channel<LocalHttpServiceDiscoveryRequest> requests = Channel.CreateBounded<LocalHttpServiceDiscoveryRequest>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
    private readonly SemaphoreSlim settingsGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly string settingsPath = Path.Combine(storageSettings.RootDirectory, "background-discovery.json");
    private HttpServiceDiscoveryPreferences preferences = HttpServiceDiscoveryPreferences.Default;
    private HttpServiceDiscoveryRunStatus status = HttpServiceDiscoveryRunStatus.Idle(HttpServiceDiscoveryPreferences.Default);
    private DateTimeOffset? lastAttemptUtc;
    private int scanPendingOrRunning;
    private CancellationTokenSource? scanCancellation;

    public HttpServiceDiscoveryPreferences GetPreferences()
    {
        lock (stateGate)
        {
            return preferences;
        }
    }

    public HttpServiceDiscoveryRunStatus GetStatus()
    {
        lock (stateGate)
        {
            return status with { NextScheduledUtc = CalculateNextScheduledUtc(preferences, lastAttemptUtc) };
        }
    }

    public bool TryStartScan(LocalHttpServiceDiscoveryRequest request)
    {
        if (Interlocked.CompareExchange(ref scanPendingOrRunning, 1, 0) != 0)
        {
            return false;
        }

        var cancellation = new CancellationTokenSource();

        lock (stateGate)
        {
            scanCancellation = cancellation;
            status = status with
            {
                IsQueued = true,
                IsScanning = false,
                Message = "HTTP/S discovery is queued in the background.",
                ProbedCount = 0,
                TotalProbeCount = 0,
                FoundCount = 0,
                Error = string.Empty
            };
        }

        if (requests.Writer.TryWrite(request))
        {
            return true;
        }

        lock (stateGate)
        {
            if (ReferenceEquals(scanCancellation, cancellation))
            {
                scanCancellation = null;
            }
        }

        cancellation.Dispose();
        Interlocked.Exchange(ref scanPendingOrRunning, 0);
        return false;
    }

    public bool TryStopScan()
    {
        lock (stateGate)
        {
            if (scanCancellation is null || scanCancellation.IsCancellationRequested)
            {
                return false;
            }

            scanCancellation.Cancel();
            return true;
        }
    }

    public async Task SavePreferencesAsync(
        HttpServiceDiscoveryPreferences updated,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePreferences(updated);
        lock (stateGate)
        {
            preferences = normalized;
            status = status with
            {
                RefreshIntervalMinutes = normalized.RefreshIntervalMinutes,
                NextScheduledUtc = CalculateNextScheduledUtc(normalized, lastAttemptUtc)
            };
        }

        await WriteSettingsAsync(cancellationToken);
    }

    public async Task<int> FlushCacheAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref scanPendingOrRunning, 1, 0) != 0)
        {
            throw new InvalidOperationException("Wait for the current discovery scan to finish before flushing the cache.");
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var discovery = scope.ServiceProvider.GetRequiredService<ILocalHttpServiceDiscoveryService>();
            var removedCount = await discovery.FlushCacheAsync(cancellationToken);
            var completed = timeProvider.GetUtcNow();
            lock (stateGate)
            {
                lastAttemptUtc = completed;
                status = HttpServiceDiscoveryRunStatus.Idle(preferences) with
                {
                    CompletedUtc = completed,
                    Message = $"Discovery cache flushed. {removedCount} cached service(s) removed.",
                    NextScheduledUtc = CalculateNextScheduledUtc(preferences, lastAttemptUtc)
                };
            }

            await WriteSettingsAsync(cancellationToken);
            logger.LogInformation("HTTP/S discovery cache flushed; {RemovedCount} cached services removed", removedCount);
            return removedCount;
        }
        finally
        {
            Interlocked.Exchange(ref scanPendingOrRunning, 0);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadSettingsAsync(stoppingToken);
        var scheduler = RunSchedulerAsync(stoppingToken);
        try
        {
            await foreach (var request in requests.Reader.ReadAllAsync(stoppingToken))
            {
                await RunScanAsync(request, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            requests.Writer.TryComplete();
            try
            {
                await scheduler;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task RunSchedulerAsync(CancellationToken stoppingToken)
    {
        await QueueScheduledScanIfDueAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await QueueScheduledScanIfDueAsync(stoppingToken);
        }
    }

    private async Task QueueScheduledScanIfDueAsync(CancellationToken cancellationToken)
    {
        HttpServiceDiscoveryPreferences current;
        DateTimeOffset? lastAttempt;
        lock (stateGate)
        {
            current = preferences;
            lastAttempt = lastAttemptUtc;
        }

        if (current.RefreshIntervalMinutes <= 0 ||
            lastAttempt.HasValue && timeProvider.GetUtcNow() < lastAttempt.Value.AddMinutes(current.RefreshIntervalMinutes))
        {
            return;
        }

        var request = current.ToRequest();
        if (TryStartScan(request))
        {
            logger.LogInformation(
                "Queued scheduled HTTP/S service discovery; refresh interval is {RefreshIntervalMinutes} minutes",
                current.RefreshIntervalMinutes);
        }

        await Task.CompletedTask;
    }

    private async Task RunScanAsync(
        LocalHttpServiceDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? scanCancellationSource;
        lock (stateGate)
        {
            scanCancellationSource = scanCancellation;
        }

        using var scanCancellationLink = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            scanCancellationSource?.Token ?? CancellationToken.None);
        var scanToken = scanCancellationLink.Token;
        var started = timeProvider.GetUtcNow();
        lock (stateGate)
        {
            lastAttemptUtc = started;
            status = status with
            {
                IsQueued = false,
                IsScanning = true,
                StartedUtc = started,
                CompletedUtc = null,
                Message = "Starting background HTTP/S discovery…",
                ProbedCount = 0,
                TotalProbeCount = 0,
                FoundCount = 0,
                Error = string.Empty
            };
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var discovery = scope.ServiceProvider.GetRequiredService<ILocalHttpServiceDiscoveryService>();
            var favourites = scope.ServiceProvider.GetService<IOnDemandAppFavouriteStore>();
            if (favourites is not null)
            {
                var savedEndpoints = (await favourites.ListAllAsync(scanToken))
                    .Select(static favourite => favourite.Endpoint)
                    .OfType<LocalHttpServiceEndpoint>();
                request = request with
                {
                    PreferredEndpoints = (request.PreferredEndpoints ?? [])
                        .Concat(savedEndpoints)
                        .DistinctBy(LocalHttpServiceDiscoveryRanking.StableKey, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                };
            }

            var progress = new Progress<LocalHttpServiceDiscoveryProgressUpdate>(update =>
            {
                lock (stateGate)
                {
                    status = status with
                    {
                        Message = update.Message,
                        ProbedCount = update.ProbedCount,
                        TotalProbeCount = update.TotalProbeCount,
                        FoundCount = update.FoundCount
                    };
                }
            });
            var services = await discovery.DiscoverAsync(request, progress, scanToken);
            if (favourites is not null)
            {
                await favourites.RefreshEndpointsAsync(services, scanToken);
            }

            var completed = timeProvider.GetUtcNow();
            lock (stateGate)
            {
                status = status with
                {
                    IsScanning = false,
                    CompletedUtc = completed,
                    Message = $"Background discovery completed. {services.Count} HTTP/S service(s) are cached.",
                    FoundCount = services.Count,
                    Error = string.Empty,
                    NextScheduledUtc = CalculateNextScheduledUtc(preferences, lastAttemptUtc)
                };
            }

            logger.LogInformation("Background HTTP/S service discovery completed with {ServiceCount} cached services", services.Count);
        }
        catch (OperationCanceledException) when (scanCancellationLink.IsCancellationRequested)
        {
            var stopped = scanCancellationSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested;
            var completed = timeProvider.GetUtcNow();
            lock (stateGate)
            {
                status = status with
                {
                    IsQueued = false,
                    IsScanning = false,
                    CompletedUtc = completed,
                    Message = stopped ? "Discovery stopped." : "Discovery stopped while LMS was shutting down.",
                    Error = string.Empty,
                    NextScheduledUtc = CalculateNextScheduledUtc(preferences, lastAttemptUtc)
                };
            }

            if (stopped)
            {
                logger.LogInformation("Background HTTP/S service discovery was stopped by the user");
            }
        }
        catch (Exception exception)
        {
            lock (stateGate)
            {
                status = status with
                {
                    IsScanning = false,
                    CompletedUtc = timeProvider.GetUtcNow(),
                    Message = "Background HTTP/S discovery failed.",
                    Error = exception.Message,
                    NextScheduledUtc = CalculateNextScheduledUtc(preferences, lastAttemptUtc)
                };
            }

            logger.LogError(exception, "Background HTTP/S service discovery failed");
        }
        finally
        {
            lock (stateGate)
            {
                if (ReferenceEquals(scanCancellation, scanCancellationSource))
                {
                    scanCancellation = null;
                }
            }

            scanCancellationSource?.Dispose();
            Interlocked.Exchange(ref scanPendingOrRunning, 0);
            await WriteSettingsAsync(CancellationToken.None);
        }
    }

    private async Task LoadSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(settingsPath))
            {
                await using var stream = File.OpenRead(settingsPath);
                var saved = await JsonSerializer.DeserializeAsync<PersistedDiscoverySettings>(stream, JsonOptions, cancellationToken);
                if (saved is not null)
                {
                    lock (stateGate)
                    {
                        preferences = NormalizePreferences(saved.Preferences);
                        lastAttemptUtc = saved.LastAttemptUtc;
                        status = HttpServiceDiscoveryRunStatus.Idle(preferences) with
                        {
                            CompletedUtc = lastAttemptUtc,
                            NextScheduledUtc = CalculateNextScheduledUtc(preferences, lastAttemptUtc)
                        };
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not load background HTTP/S discovery settings; using defaults");
        }
    }

    private async Task WriteSettingsAsync(CancellationToken cancellationToken)
    {
        await settingsGate.WaitAsync(cancellationToken);
        try
        {
            PersistedDiscoverySettings saved;
            lock (stateGate)
            {
                saved = new PersistedDiscoverySettings(preferences, lastAttemptUtc);
            }

            Directory.CreateDirectory(storageSettings.RootDirectory);
            var temporaryPath = $"{settingsPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 16_384,
                                 FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(stream, saved, JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

                File.Move(temporaryPath, settingsPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            settingsGate.Release();
        }
    }

    private DateTimeOffset? CalculateNextScheduledUtc(
        HttpServiceDiscoveryPreferences current,
        DateTimeOffset? lastAttempt) =>
        current.RefreshIntervalMinutes <= 0
            ? null
            : (lastAttempt ?? timeProvider.GetUtcNow()).AddMinutes(current.RefreshIntervalMinutes);

    private static HttpServiceDiscoveryPreferences NormalizePreferences(HttpServiceDiscoveryPreferences value) =>
        value with
        {
            RefreshIntervalMinutes = AllowedRefreshIntervals.Contains(value.RefreshIntervalMinutes)
                ? value.RefreshIntervalMinutes
                : HttpServiceDiscoveryPreferences.Default.RefreshIntervalMinutes
        };

    private sealed record PersistedDiscoverySettings(
        HttpServiceDiscoveryPreferences Preferences,
        DateTimeOffset? LastAttemptUtc);
}

public sealed record HttpServiceDiscoveryPreferences(
    int RefreshIntervalMinutes,
    bool IncludeDocker,
    bool IncludeLan,
    bool IncludeTailnet)
{
    public static HttpServiceDiscoveryPreferences Default { get; } = new(180, true, true, false);

    public LocalHttpServiceDiscoveryRequest ToRequest() =>
        new(true, IncludeLan, IncludeTailnet, IncludeDocker);
}

public sealed record HttpServiceDiscoveryRunStatus(
    bool IsQueued,
    bool IsScanning,
    string Message,
    int ProbedCount,
    int TotalProbeCount,
    int FoundCount,
    string Error,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    DateTimeOffset? NextScheduledUtc,
    int RefreshIntervalMinutes)
{
    public bool IsActive => IsQueued || IsScanning;

    public static HttpServiceDiscoveryRunStatus Idle(HttpServiceDiscoveryPreferences preferences) =>
        new(false, false, "Discovery is idle.", 0, 0, 0, string.Empty, null, null, null, preferences.RefreshIntervalMinutes);
}
