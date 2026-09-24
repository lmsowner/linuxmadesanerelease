// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record FirewallTrialViewModel(
    Guid Id,
    string Title,
    string Detail,
    DateTimeOffset ExpiresAtUtc);
