// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Connect.Protocol.Models;

public sealed record PortalPendingPackage(string Name,string CurrentVersion,string CandidateVersion,bool IsSecurity);
public sealed record PortalPackageUpdateDetails(
    IReadOnlyList<PortalPendingPackage> Packages,int PendingCount,DateTimeOffset? CheckedAtUtc,
    IReadOnlyList<string> LogLines,int ProgressPercent,string Summary,bool PlannedPackages=false,
    bool PackagesTruncated=false,bool LogTruncated=false)
{
    public DateTimeOffset ObservedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
