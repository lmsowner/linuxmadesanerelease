// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record SecurityUserPasswordResetViewModel(
    Guid UserId,
    string Email,
    string LinuxUsername,
    string SuggestedPassword);
