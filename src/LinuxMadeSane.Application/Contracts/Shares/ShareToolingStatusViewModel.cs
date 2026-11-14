// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record ShareToolingStatusViewModel(
    bool HasAllRequiredPackages,
    bool CanScanNetwork,
    bool CanBrowseRemoteShares,
    bool CanCreateRemoteMounts,
    bool IsLocalHostRegistered,
    string? LocalHostName,
    string StatusMessage,
    string InstallCommand,
    IReadOnlyList<string> MissingPackageNames,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ShareToolingPackageViewModel> Packages);

public sealed record ShareToolingPackageViewModel(
    string PackageName,
    string Purpose,
    IReadOnlyList<string> Commands,
    bool IsInstalled,
    string Version);
