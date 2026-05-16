// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record EffectiveAccessCheckerViewModel(
    IReadOnlyList<SambaShareDefinition> AvailableShares,
    EffectiveAccessCheckResult Result);
