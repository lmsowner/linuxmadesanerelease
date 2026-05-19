// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
