// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Security.Cryptography;
using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Cloudflare;
using LinuxMadeSane.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;

namespace LinuxMadeSane.Web.Services;

public sealed record PagePreview(string? DataUrl, string Message, DateTimeOffset? ExpiresUtc = null);
public sealed record PagePreviewWarmupProgress(int Completed, int Total, int Available);
public interface IHttpServicePageRenderer
{
    Task<byte[]> RenderAsync(Uri origin, CancellationToken cancellationToken);
}

/// <summary>Persistent, bounded, anonymous screenshots. Restarting LMS never invalidates the cache.</summary>
public sealed class HttpServicePagePreview(
    ILocalHttpServiceDiscoveryService discovery,
    HttpServiceDiscoveryStorageSettings storage,
    IHttpServicePageRenderer renderer,
    ILinuxCommandRunner runner,
    TimeProvider clock,
    ILogger<HttpServicePagePreview>? logger = null) : IAsyncDisposable
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
    private static readonly SemaphoreSlim renderGate = new(1, 1);
    private static readonly MemoryCache memory = new(new MemoryCacheOptions { SizeLimit = 16 * 1024 * 1024 });
    private readonly string directory = Path.Combine(storage.RootDirectory, "page-previews");
    private readonly object queueLock = new();
    private readonly LinkedList<PreviewRequest> pending = new();
    private readonly Dictionary<string, PreviewRequest> queued = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource queueCancellation = new();
    private Task? queueWorker;
    private bool disposed;

    private sealed class PreviewRequest(LocalHttpServiceEndpoint endpoint, CancellationToken cancellation)
    {
        public LocalHttpServiceEndpoint Endpoint { get; } = endpoint;
        public CancellationToken Cancellation { get; set; } = cancellation;
        public bool Priority { get; set; }
        public LinkedListNode<PreviewRequest>? Node { get; set; }
        public TaskCompletionSource<PagePreview> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public bool Installed => ChromiumPageRenderer.BrowserExecutable is not null;

    public async Task InstallAsync(CancellationToken ct = default)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("LMS executable unavailable.");
        string[] arguments = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [typeof(Program).Assembly.Location, ChromiumPageRenderer.InstallCommand] : [ChromiumPageRenderer.InstallCommand];
        var result = await runner.RunAsync(new(executable, arguments, true, TimeSpan.FromMinutes(10),
            "Install sandboxed Chromium homepage previews"), false, ct);
        if (result.ExitCode != 0 || !Installed)
            throw new InvalidOperationException("Chromium installation failed. Check the installation log; discovery remains available.");
    }

    public bool TryGetCached(LocalHttpServiceEndpoint endpoint, out PagePreview? preview)
    {
        var path = CachePath(endpoint);
        if (memory.TryGetValue(path, out preview) && preview?.ExpiresUtc > clock.GetUtcNow()) return true;
        memory.Remove(path);
        preview = null;
        return false;
    }

    public async Task<PagePreview> GetAsync(LocalHttpServiceEndpoint endpoint, CancellationToken ct = default)
    {
        try { return await GetCoreAsync(endpoint, ct); }
        // Playwright can throw a non-cancellation exception when cancellation closes its browser.
        // A quick pointer move must never kill the Blazor circuit or cache a false site failure.
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not OutOfMemoryException)
        { throw new OperationCanceledException("Homepage preview cancelled.", ex, ct); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return await FailureAsync(endpoint, ex); }
    }

    /// <summary>Hover/focus promotes an existing warm-up request. Leaving the card only cancels the wait.</summary>
    public async Task<PagePreview> GetQueuedAsync(LocalHttpServiceEndpoint endpoint, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try { return await Enqueue(endpoint, priority: true, CancellationToken.None).WaitAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return await FailureAsync(endpoint, ex); }
    }

    public async Task<PagePreviewWarmupProgress> WarmAsync(IEnumerable<LocalHttpServiceEndpoint> endpoints, IProgress<PagePreviewWarmupProgress>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var requests = endpoints.Where(x => x.Exposure != DiscoveryExposure.UnsafeToExpose)
            .DistinctBy(x => CachePath(x)).Select(x => Enqueue(x, priority: false, ct)).ToList();
        var total = requests.Count; var completed = 0; var available = 0;
        progress?.Report(new(0, total, 0));
        while (requests.Count > 0)
        {
            var finished = await Task.WhenAny(requests).WaitAsync(ct);
            requests.Remove(finished);
            var result = await finished;
            if (result.DataUrl is not null) available++;
            completed++;
            progress?.Report(new(completed, total, available));
        }
        return new(completed, total, available);
    }

    private Task<PagePreview> Enqueue(LocalHttpServiceEndpoint endpoint, bool priority, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (TryGetCached(endpoint, out var cached)) return Task.FromResult(cached!);
        var key = CachePath(endpoint);
        lock (queueLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (queued.TryGetValue(key, out var request) && request.Completion.Task.IsCompleted)
            { queued.Remove(key); request = null; }
            if (request is null)
            {
                request = new(endpoint, cancellation);
                request.Node = priority ? pending.AddFirst(request) : pending.AddLast(request);
                queued.Add(key, request);
            }
            else if (!priority && !request.Priority)
                request.Cancellation = cancellation; // A reopened dialog can resume the same pending origin.
            if (priority)
            {
                request.Priority = true;
                if (request.Node is { } node) { pending.Remove(node); pending.AddFirst(node); }
            }
            queueWorker ??= Task.Run(ProcessQueueAsync);
            return request.Completion.Task;
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (true)
            {
                // Reserve the single global browser slot before selecting the next item,
                // so a hover can promote a waiting request even while another scope renders.
                await renderGate.WaitAsync(queueCancellation.Token);
                PreviewRequest? request = null;
                try
                {
                    lock (queueLock)
                    {
                        if (pending.First is not { } node) { queueWorker = null; return; }
                        request = node.Value; pending.RemoveFirst(); request.Node = null;
                    }
                    if (!request.Priority && request.Cancellation.IsCancellationRequested)
                    {
                        request.Completion.TrySetCanceled(request.Cancellation);
                        continue;
                    }
                    // Closing a dialog skips its waiting background work. An active capture
                    // finishes into the cache, unless its service scope is being disposed.
                    request.Completion.TrySetResult(await GetCoreAsync(request.Endpoint, queueCancellation.Token, renderSlotHeld: true));
                }
                catch (OperationCanceledException) { request?.Completion.TrySetCanceled(); }
                catch (Exception ex) when (queueCancellation.IsCancellationRequested && ex is not OutOfMemoryException)
                { request?.Completion.TrySetCanceled(); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (request is not null) request.Completion.TrySetResult(await FailureAsync(request.Endpoint, ex));
                }
                finally
                {
                    if (request is not null) lock (queueLock)
                    {
                        var key = CachePath(request.Endpoint);
                        if (queued.TryGetValue(key, out var current) && ReferenceEquals(current, request)) queued.Remove(key);
                    }
                    renderGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (queueCancellation.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        Task? worker;
        lock (queueLock)
        {
            if (disposed) return;
            disposed = true; queueCancellation.Cancel(); worker = queueWorker;
            foreach (var request in queued.Values) request.Completion.TrySetCanceled();
            pending.Clear();
        }
        if (worker is not null) await worker;
        queueCancellation.Dispose();
    }

    private async Task<PagePreview> FailureAsync(LocalHttpServiceEndpoint endpoint, Exception ex)
    {
        logger?.LogWarning("Homepage preview failed ({ErrorType}); the app dialog remains available", ex.GetType().Name);
        var fallback = new PagePreview(null, "Homepage preview unavailable. You can continue using this dialog.", clock.GetUtcNow() + RetryDelay);
        try
        {
            var path = CachePath(endpoint);
            // A failed refresh keeps the last successful screenshot visible.
            if (await ReadAsync(path, CancellationToken.None, allowExpired: true) is { } old)
                fallback = old with { Message = "Cached homepage; refresh unavailable.", ExpiresUtc = clock.GetUtcNow() + RetryDelay };
            Remember(path, fallback);
        }
        catch (Exception cacheError) when (cacheError is not OutOfMemoryException)
        { /* The renderer and its optional cache must not bring down an app editor. */ }
        return fallback;
    }

    private async Task<PagePreview> GetCoreAsync(LocalHttpServiceEndpoint endpoint, CancellationToken ct, bool renderSlotHeld = false)
    {
        ct.ThrowIfCancellationRequested();
        if (TryGetCached(endpoint, out var hit)) return hit!;
        var path = CachePath(endpoint);
        if (await ReadAsync(path, ct) is { } cached) return Remember(path, cached);
        if (!renderSlotHeld) await renderGate.WaitAsync(ct);
        try
        {
            // Different sessions and recreated cards share the same completed image.
            if (TryGetCached(endpoint, out hit)) return hit!;
            if (await ReadAsync(path, ct) is { } concurrent) return Remember(path, concurrent);
            // Only a server-owned discovery record can supply a new render destination.
            var known = (await discovery.GetCachedAsync(ct)).FirstOrDefault(x =>
                x.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase) && x.Port == endpoint.Port &&
                x.Scheme.Equals(endpoint.Scheme, StringComparison.OrdinalIgnoreCase));
            if (known is null || known.Exposure == DiscoveryExposure.UnsafeToExpose)
                return new(null, "This service is no longer available for preview.", clock.GetUtcNow() + RetryDelay);
            var origin = Origin(known);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var image = await renderer.RenderAsync(origin, deadline.Token);
            if (image.Length is < 4 or > 1024 * 1024 || image[0] != 0xff || image[1] != 0xd8)
                throw new InvalidOperationException("The homepage did not produce a valid preview.");
            Directory.CreateDirectory(directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var captured = clock.GetUtcNow();
            try
            {
                await File.WriteAllBytesAsync(temporary, image, ct);
                File.Move(temporary, path, true);
                File.SetLastWriteTimeUtc(path, captured.UtcDateTime);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            // Only generated preview files are managed here. No source documents or mounts.
            foreach (var old in new DirectoryInfo(directory).GetFiles("*.jpg")
                         .OrderByDescending(x => x.LastWriteTimeUtc).Skip(256)) old.Delete();
            return Remember(path, FromImage(image, captured + CacheLifetime));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException)
        { return await FailureAsync(endpoint, ex); }
        finally { if (!renderSlotHeld) renderGate.Release(); }
    }

    private string CachePath(LocalHttpServiceEndpoint endpoint)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Origin(endpoint).AbsoluteUri))).ToLowerInvariant();
        return Path.Combine(directory, key + ".jpg");
    }

    private PagePreview Remember(string path, PagePreview preview)
    {
        var lifetime = (preview.ExpiresUtc ?? clock.GetUtcNow() + RetryDelay) - clock.GetUtcNow();
        if (lifetime > TimeSpan.Zero)
            memory.Set(path, preview, new MemoryCacheEntryOptions
            {
                Size = (preview.DataUrl?.Length ?? 0) * 2L + 512,
                AbsoluteExpirationRelativeToNow = lifetime
            });
        return preview;
    }

    private async Task<PagePreview?> ReadAsync(string path, CancellationToken ct, bool allowExpired = false)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < 4 or > 1024 * 1024) return null;
            var expires = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) + CacheLifetime;
            if (!allowExpired && expires <= clock.GetUtcNow()) return null;
            var bytes = await File.ReadAllBytesAsync(path, ct);
            return bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8 ? FromImage(bytes, expires) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    private static PagePreview FromImage(byte[] image, DateTimeOffset expires) => new("data:image/jpeg;base64," + Convert.ToBase64String(image), "Cached anonymous homepage screenshot", expires);
    public static Uri Origin(LocalHttpServiceEndpoint service) => service.Scheme is "http" or "https" && service.Port is > 0 and <= 65535
        ? new UriBuilder(service.Scheme, service.Host, service.Port, "/").Uri : throw new ArgumentException("Invalid discovered service.");
}
