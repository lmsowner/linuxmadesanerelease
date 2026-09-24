// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Runtime.InteropServices;
using LinuxMadeSane.Application.Contracts.SystemInfo;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Versioning;

namespace LinuxMadeSane.Infrastructure.Services.ConfigurationSummary;

public sealed class SystemConfigurationSummaryProvider(ILocalSystemMonitorService systemMonitor)
    : ILmsConfigurationSummaryProvider
{
    public string ModuleName => "System";

    public int SortOrder => 10;

    public async Task<LmsConfigurationSummary> GetConfigurationSummaryAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await systemMonitor.CaptureAsync(cancellationToken);
        return new LmsConfigurationSummary(
            ModuleName,
            LmsConfigurationSummaryStatus.Configured,
            "Local LMS host",
            [
                new("Hostname", snapshot.HostName),
                new("Operating system", snapshot.OperatingSystem),
                new("Architecture", RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()),
                new("Kernel", snapshot.KernelVersion),
                new("LMS version", LinuxMadeSaneBuildVersion.GetCurrent(typeof(SystemConfigurationSummaryProvider).Assembly))
            ],
            [],
            null,
            SortOrder);
    }
}
