// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record SecurityUserProvisioningViewModel(
    Guid UserId,
    string Email,
    string LinuxUsername,
    RemoteAccessSshAuthenticationMode SshAuthenticationMode,
    string ManualEntryKey,
    string OtpUri,
    bool EmailAttempted,
    bool EmailSucceeded,
    string EmailMessage);
