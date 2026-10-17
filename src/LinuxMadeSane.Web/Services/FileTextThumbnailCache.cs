// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using Microsoft.Extensions.Caching.Memory;

namespace LinuxMadeSane.Web.Services;

// Small, access-scoped excerpts: never read an entire log or persist its contents to disk.
public sealed class FileTextThumbnailCache : IDisposable
{
    public const int MaxReadBytes = 4096;
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 2 * 1024 * 1024 });
    private readonly SemaphoreSlim reads = new(1);

    public static bool Supports(string name) => FileTextExtensions.Contains(Path.GetExtension(name)) || name.Contains(".log.", StringComparison.OrdinalIgnoreCase) ||
        new[] { "README", "LICENSE", "syslog", "messages", "Dockerfile", "Makefile", ".env", ".gitignore" }.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> FileTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".out", ".err", ".rst", ".adoc", ".svg", ".log", ".md", ".json", ".yaml", ".yml", ".xml", ".html", ".htm", ".css", ".scss", ".less",
        ".js", ".ts", ".jsx", ".tsx", ".cs", ".py", ".sh", ".bash", ".zsh", ".ini", ".cfg", ".conf",
        ".config", ".toml", ".env", ".properties", ".service", ".socket", ".timer", ".target", ".sql", ".csv", ".tsv"
    };

    public async Task<string> GetAsync(ManagedHost host, SftpItem item, ManagedHostConnectionProfile profile,
        IManagedHostFileAccessService access, CancellationToken token)
    {
        var key = $"{host.Id}|{item.FullPath}|{item.SizeBytes}|{item.LastModifiedUtc}|{profile}";
        if (cache.TryGetValue<string>(key, out var hit)) return hit!;
        await reads.WaitAsync(token);
        try
        {
            if (cache.TryGetValue<string>(key, out hit)) return hit!;
            var content = await access.ReadFileAsync(host, item.FullPath, profile, MaxReadBytes, token);
            token.ThrowIfCancellationRequested();
            var excerpt = BuildExcerpt(content.Content);
            cache.Set(key, excerpt, new MemoryCacheEntryOptions { Size = Math.Max(1, excerpt.Length * 2), SlidingExpiration = TimeSpan.FromMinutes(5) });
            return excerpt;
        }
        finally { reads.Release(); }
    }

    public static string BuildExcerpt(string content)
    {
        if (content.Contains('\0') || content.Count(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t') > Math.Max(1, content.Length / 40)) return "";
        return string.Join("\n", content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Take(12)
            .Select(line => line.Length > 160 ? line[..160] + "…" : line)).TrimEnd();
    }

    public void Dispose() { cache.Dispose(); reads.Dispose(); }
}
