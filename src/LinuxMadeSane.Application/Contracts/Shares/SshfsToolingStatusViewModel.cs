// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record SshfsToolingStatusViewModel(
    bool HasAllRequiredPackages,
    bool CanCreateSshfsMounts,
    bool IsLocalHostRegistered,
    string? LocalHostName,
    string StatusMessage,
    string InstallCommand,
    IReadOnlyList<string> MissingPackageNames,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ShareToolingPackageViewModel> Packages);
