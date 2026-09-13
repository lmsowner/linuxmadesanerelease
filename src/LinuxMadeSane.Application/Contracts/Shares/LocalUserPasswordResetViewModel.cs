// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record LocalUserPasswordResetViewModel(
    Guid UserId,
    string UserName,
    string SuggestedPassword);
