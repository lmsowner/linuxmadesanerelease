// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using System.Diagnostics;
using System.Globalization;

namespace LinuxMadeSane.Infrastructure.Services;

internal static class LocalFileBrowsingSupport
{
    public static string NormalizePath(string workingDirectory, string path)
    {
        var normalized = string.IsNullOrWhiteSpace(path)
            ? workingDirectory
            : path.Trim();

        return Path.IsPathRooted(normalized)
            ? Path.GetFullPath(normalized)
            : Path.GetFullPath(normalized, workingDirectory);
    }

    public static SftpItem MapItem(FileSystemInfo item)
    {
        var linkTarget = item.LinkTarget ?? string.Empty;
        var itemType = !string.IsNullOrWhiteSpace(linkTarget)
            ? SftpItemType.Link
            : item.Attributes.HasFlag(FileAttributes.Directory)
                ? SftpItemType.Folder
                : SftpItemType.File;
        var sizeBytes = item is FileInfo fileInfo ? fileInfo.Length : 0;
        var metadata = ReadMetadata(item, itemType);

        return new SftpItem(
            item.Name,
            item.FullName,
            itemType,
            sizeBytes,
            item.LastWriteTimeUtc == DateTime.MinValue ? null : new DateTimeOffset(item.LastWriteTimeUtc, TimeSpan.Zero),
            metadata.SymbolicPermissions,
            metadata.OwnerName,
            metadata.GroupName,
            metadata.OctalPermissions,
            linkTarget);
    }

    private static LocalFileMetadata ReadMetadata(FileSystemInfo item, SftpItemType itemType)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var statMetadata = TryReadStatMetadata(item);
            if (statMetadata is not null)
            {
                return statMetadata;
            }
        }

        var mode = TryGetUnixFileMode(item);
        return new LocalFileMetadata(
            string.Empty,
            string.Empty,
            FormatPermissions(mode, itemType),
            FormatPermissionsOctal(mode));
    }

    private static LocalFileMetadata? TryReadStatMetadata(FileSystemInfo item)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("stat")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };

            process.StartInfo.ArgumentList.Add("--printf=%U\\0%G\\0%A\\0%a");
            process.StartInfo.ArgumentList.Add("--");
            process.StartInfo.ArgumentList.Add(item.FullName);

            if (!process.Start())
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(1_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return null;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            var parts = output.Split('\0');
            return parts.Length >= 4
                ? new LocalFileMetadata(parts[0], parts[1], parts[2], parts[3])
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static UnixFileMode TryGetUnixFileMode(FileSystemInfo item)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return UnixFileMode.None;
        }

        try
        {
            return item.UnixFileMode;
        }
        catch
        {
            return UnixFileMode.None;
        }
    }

    private static string FormatPermissions(UnixFileMode mode, SftpItemType itemType) =>
        string.Create(10, (mode, itemType), static (span, state) =>
        {
            span[0] = state.itemType switch
            {
                SftpItemType.Folder => 'd',
                SftpItemType.Link => 'l',
                _ => '-'
            };
            span[1] = state.mode.HasFlag(UnixFileMode.UserRead) ? 'r' : '-';
            span[2] = state.mode.HasFlag(UnixFileMode.UserWrite) ? 'w' : '-';
            span[3] = state.mode.HasFlag(UnixFileMode.UserExecute) ? 'x' : '-';
            span[4] = state.mode.HasFlag(UnixFileMode.GroupRead) ? 'r' : '-';
            span[5] = state.mode.HasFlag(UnixFileMode.GroupWrite) ? 'w' : '-';
            span[6] = state.mode.HasFlag(UnixFileMode.GroupExecute) ? 'x' : '-';
            span[7] = state.mode.HasFlag(UnixFileMode.OtherRead) ? 'r' : '-';
            span[8] = state.mode.HasFlag(UnixFileMode.OtherWrite) ? 'w' : '-';
            span[9] = state.mode.HasFlag(UnixFileMode.OtherExecute) ? 'x' : '-';
        });

    private static string FormatPermissionsOctal(UnixFileMode mode)
    {
        var special = 0;
        if (mode.HasFlag(UnixFileMode.SetUser))
        {
            special += 4;
        }

        if (mode.HasFlag(UnixFileMode.SetGroup))
        {
            special += 2;
        }

        if (mode.HasFlag(UnixFileMode.StickyBit))
        {
            special += 1;
        }

        var owner = (mode.HasFlag(UnixFileMode.UserRead) ? 4 : 0) +
                    (mode.HasFlag(UnixFileMode.UserWrite) ? 2 : 0) +
                    (mode.HasFlag(UnixFileMode.UserExecute) ? 1 : 0);
        var group = (mode.HasFlag(UnixFileMode.GroupRead) ? 4 : 0) +
                    (mode.HasFlag(UnixFileMode.GroupWrite) ? 2 : 0) +
                    (mode.HasFlag(UnixFileMode.GroupExecute) ? 1 : 0);
        var other = (mode.HasFlag(UnixFileMode.OtherRead) ? 4 : 0) +
                    (mode.HasFlag(UnixFileMode.OtherWrite) ? 2 : 0) +
                    (mode.HasFlag(UnixFileMode.OtherExecute) ? 1 : 0);

        return special > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{special}{owner}{group}{other}")
            : string.Create(CultureInfo.InvariantCulture, $"{owner}{group}{other}");
    }

    private sealed record LocalFileMetadata(
        string OwnerName,
        string GroupName,
        string SymbolicPermissions,
        string OctalPermissions);
}
