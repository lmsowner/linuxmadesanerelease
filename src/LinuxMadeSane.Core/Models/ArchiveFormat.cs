// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public enum ArchiveFormat
{
    Zip = 0,
    SevenZip = 1,
    Gzip = 2,
    TarGzip = 3,
    Rar = 4
}

public sealed record ArchiveEntry(
    string Name,
    string FullName,
    SftpItemType ItemType,
    long SizeBytes,
    DateTimeOffset? LastModifiedUtc);

public static class ArchiveFormatSupport
{
    public static bool TryResolveFromFileName(string? fileName, out ArchiveFormat format)
    {
        var normalized = fileName?.Trim() ?? string.Empty;
        if (normalized.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            format = ArchiveFormat.TarGzip;
            return true;
        }

        var extension = Path.GetExtension(normalized);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            format = ArchiveFormat.Zip;
            return true;
        }

        if (extension.Equals(".7z", StringComparison.OrdinalIgnoreCase))
        {
            format = ArchiveFormat.SevenZip;
            return true;
        }

        if (extension.Equals(".gz", StringComparison.OrdinalIgnoreCase))
        {
            format = ArchiveFormat.Gzip;
            return true;
        }

        if (extension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            format = ArchiveFormat.Rar;
            return true;
        }

        format = ArchiveFormat.Zip;
        return false;
    }

    public static string GetExtension(ArchiveFormat format, bool sourceIsDirectory) => format switch
    {
        ArchiveFormat.Zip => ".zip",
        ArchiveFormat.SevenZip => ".7z",
        ArchiveFormat.Gzip when sourceIsDirectory => ".tar.gz",
        ArchiveFormat.Gzip => ".gz",
        ArchiveFormat.TarGzip => ".tar.gz",
        ArchiveFormat.Rar => ".rar",
        _ => ".zip"
    };

    public static string GetDisplayName(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => "Zip",
        ArchiveFormat.SevenZip => "7Zip",
        ArchiveFormat.Gzip => "Gzip",
        ArchiveFormat.TarGzip => "Tar.gz",
        ArchiveFormat.Rar => "RAR",
        _ => "Archive"
    };
}
