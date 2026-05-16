// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record LocalUserPasswordResetViewModel(
    Guid UserId,
    string UserName,
    string SuggestedPassword);
