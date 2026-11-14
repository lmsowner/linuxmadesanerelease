// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Collections.Concurrent;
using System.IO.Compression;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Services;

public sealed partial class BrowserFileTransferService
{
    private readonly ConcurrentDictionary<string, DesktopDownload> desktopDownloads = new();
    private readonly Timer desktopCleanup;

    // File-scoped capabilities allow the OS download process to fetch the promised file
    // without accepting a host/path or credentials from an HTTP request.
    public string RegisterDesktopDownload(IManagedHostFileAccessService access, ManagedHost host,
        ManagedHostConnectionProfile profile, string source, string fileName, string[]? zipEntries = null, bool zipSelection = false)
    {
        ExpireDesktopDownloads();
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var entry = new DesktopDownload(Path.Combine(transferRoot, "desktop-" + token), SanitizeFileName(fileName),
            async (directory, ct) =>
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
                else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var original = Path.Combine(directory, "source");
                await access.DownloadFileAsync(host, source, original, profile, cancellationToken: ct);
                if (zipEntries is null) return original;
                var output = Path.Combine(directory, "export");
                await ExportZipSelectionAsync(original, output, zipEntries, zipSelection, ct);
                File.Delete(original);
                return output;
            });
        desktopDownloads[token] = entry;
        return "internal/desktop-downloads/" + token;
    }

    private void ExpireDesktopDownloads()
    {
        foreach (var pair in desktopDownloads)
            if (pair.Value.Expires < DateTimeOffset.UtcNow && desktopDownloads.TryRemove(pair.Key, out var entry)) entry.Dispose();
    }

    private static void MapDesktopDownloadEndpoints(WebApplication app)
    {
        app.MapMethods("/internal/desktop-downloads/{token}", ["GET", "HEAD"], async (string token, HttpContext context, BrowserFileTransferService transfers) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (!transfers.desktopDownloads.TryGetValue(token, out var entry) || entry.Expires < DateTimeOffset.UtcNow) return Results.NotFound();
            try
            {
                var path = await entry.GetPathAsync(context.RequestAborted);
                return Results.File(path, "application/octet-stream", entry.Name, enableRangeProcessing: true);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
            { return Results.Problem("The file could not be prepared. Retry the download from LMS."); }
        });
    }

    internal static async Task ExportZipSelectionAsync(string source, string destination, string[] selected, bool asZip, CancellationToken ct)
    {
        static string Clean(string name)
        {
            if (name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Contains('\0')) throw new InvalidDataException("Unsafe ZIP entry.");
            var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToArray();
            if (parts.Contains("..")) throw new InvalidDataException("Unsafe ZIP entry.");
            return string.Join('/', parts);
        }
        selected = selected.Select(Clean).ToArray();
        using var archive = ZipFile.OpenRead(source);
        var entries = archive.Entries.Select(e => (Entry: e, Path: Clean(e.FullName)))
            .Where(e => e.Path.Length > 0 && (selected.Length == 0 || selected.Any(s => e.Path == s || e.Path.StartsWith(s + "/", StringComparison.Ordinal)))).ToArray();
        if (entries.Length == 0 || entries.Length > 100_000 || entries.Sum(e => e.Entry.Length) > 20L * 1024 * 1024 * 1024) throw new InvalidDataException("ZIP selection is empty or too large.");
        if (entries.Any(e => ((e.Entry.ExternalAttributes >> 16) & 0xF000) is not (0 or 0x8000 or 0x4000))) throw new InvalidDataException("ZIP links and special files cannot be exported.");
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
        if (!asZip)
        {
            if (entries.Length != 1 || entries[0].Entry.FullName.EndsWith('/')) throw new InvalidDataException("Choose a single file.");
            await using var data = entries[0].Entry.Open();
            await data.CopyToAsync(output, ct);
            return;
        }
        using var export = new ZipArchive(output, ZipArchiveMode.Create, true);
        foreach (var item in entries)
        {
            ct.ThrowIfCancellationRequested();
            var directory = item.Entry.FullName.EndsWith('/');
            var entry = export.CreateEntry(item.Path + (directory ? "/" : ""), CompressionLevel.Fastest);
            if (directory) continue;
            await using var data = item.Entry.Open();
            await using var target = entry.Open();
            await data.CopyToAsync(target, ct);
        }
    }

    private sealed class DesktopDownload(string directory, string name, Func<string, CancellationToken, Task<string>> prepare) : IDisposable
    {
        private readonly SemaphoreSlim gate = new(1);
        private readonly CancellationTokenSource lifetime = new();
        private string? path;
        public string Name { get; } = name;
        public DateTimeOffset Expires { get; } = DateTimeOffset.UtcNow.AddMinutes(30);
        public async Task<string> GetPathAsync(CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            await gate.WaitAsync(linked.Token);
            try
            {
                if (path is not null) return path;
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                path = await prepare(directory, linked.Token);
                return path;
            }
            finally { gate.Release(); }
        }
        public void Dispose()
        {
            lifetime.Cancel();
            _ = CleanupAsync();
        }
        private async Task CleanupAsync()
        {
            await gate.WaitAsync();
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch (IOException) { }
            finally { gate.Release(); }
        }
    }
}
