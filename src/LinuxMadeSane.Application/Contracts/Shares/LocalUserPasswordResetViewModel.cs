// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record LocalUserPasswordResetViewModel(
    Guid UserId,
    string UserName,
    string SuggestedPassword);
