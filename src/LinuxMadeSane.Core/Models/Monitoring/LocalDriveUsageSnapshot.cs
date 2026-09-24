// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.Monitoring;

public sealed record LocalDriveUsageSnapshot(
    string Path,
    string? ParentPath,
    DateTimeOffset ScannedAtUtc,
    long TotalBytes,
    long FileCount,
    long DirectoryCount,
    long SkippedItemCount,
    bool IsPartial,
    LocalDriveFileSystemMetric FileSystem,
    IReadOnlyList<LocalDriveUsageItem> Items)
{
    public bool AccessWasPrivileged { get; init; }

    public string? AccessWarning { get; init; }
}

public sealed record LocalDriveFileSystemMetric(
    string Name,
    string Format,
    long TotalBytes,
    long UsedBytes,
    long AvailableBytes);

public sealed record LocalDriveUsageItem(
    string Name,
    string FullPath,
    bool IsDirectory,
    long UsedBytes,
    double UsagePercent,
    long FileCount,
    long DirectoryCount,
    DateTimeOffset? ModifiedAtUtc,
    bool IsPartial);
