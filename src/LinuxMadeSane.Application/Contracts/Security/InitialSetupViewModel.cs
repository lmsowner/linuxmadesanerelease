// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record InitialSetupViewModel(
    bool IsComplete,
    bool CanStart,
    bool CanVerify,
    Guid? PendingUserId,
    string PendingEmail,
    string PendingLinuxUsername,
    string SuggestedLinuxUsername,
    string InstallerLinuxUsername,
    string InstallerHomeDirectory,
    bool HasInstallerIdentity,
    int UserCount,
    string StatusMessage);
