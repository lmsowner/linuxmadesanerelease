// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record InitialSetupViewModel(
    bool IsComplete,
    bool CanStart,
    bool CanVerify,
    bool AwaitingFirstLogin,
    Guid? PendingUserId,
    string PendingEmail,
    string PendingLinuxUsername,
    string SuggestedLinuxUsername,
    string InstallerLinuxUsername,
    string InstallerHomeDirectory,
    bool HasInstallerIdentity,
    int UserCount,
    string StatusMessage);
