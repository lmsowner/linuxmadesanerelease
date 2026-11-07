// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record ShareManagerViewModel(
    IReadOnlyList<SambaShareDefinition> Shares,
    SambaShareSystemCheckResult? SystemCheck = null);
