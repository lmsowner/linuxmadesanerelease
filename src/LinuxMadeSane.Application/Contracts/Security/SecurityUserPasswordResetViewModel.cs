// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record SecurityUserPasswordResetViewModel(
    Guid UserId,
    string Email,
    string LinuxUsername,
    string SuggestedPassword);
