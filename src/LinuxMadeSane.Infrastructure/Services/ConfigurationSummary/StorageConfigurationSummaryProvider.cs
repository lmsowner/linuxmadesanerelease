// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.SystemInfo;
using LinuxMadeSane.Application.Interfaces;

namespace LinuxMadeSane.Infrastructure.Services.ConfigurationSummary;

public sealed class StorageConfigurationSummaryProvider(IStorageDiscoveryService discovery)
    : ILmsConfigurationSummaryProvider
{
    public string ModuleName => "Storage";

    public int SortOrder => 35;

    public string NavigationUrl => "/storage";

    public async Task<LmsConfigurationSummary> GetConfigurationSummaryAsync(CancellationToken cancellationToken = default)
    {
        var topology = await discovery.DiscoverAsync(cancellationToken);
        var resizable = topology.FileSystems.Count(fileSystem => fileSystem.CanGrow || fileSystem.CanShrink);
        var unused = topology.Disks.Sum(disk => disk.UnallocatedBytes);
        var warnings = topology.Warnings.Take(2).ToArray();
        return new LmsConfigurationSummary(
            ModuleName,
            warnings.Length > 0 ? LmsConfigurationSummaryStatus.Warning : LmsConfigurationSummaryStatus.Configured,
            "Local disks and filesystem resize workflows",
            [
                new("Disks", topology.Disks.Count.ToString()),
                new("Mounted filesystems", topology.FileSystems.Count.ToString()),
                new("Resize available", resizable.ToString()),
                new("Unallocated disk space", FormatBytes(unused))
            ],
            warnings,
            NavigationUrl,
            SortOrder);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "None";
        var gib = bytes / (double)(1024L * 1024L * 1024L);
        return gib >= 1024 ? $"{gib / 1024:0.##} TB" : $"{gib:0.##} GB";
    }
}
