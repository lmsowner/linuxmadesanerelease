// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

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
