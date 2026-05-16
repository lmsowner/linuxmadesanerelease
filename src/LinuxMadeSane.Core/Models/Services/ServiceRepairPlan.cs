// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Services;

public sealed record ServiceRepairPlan(
    Guid ServiceId,
    string UnitName,
    IReadOnlyList<ServiceRepairIssue> Issues,
    IReadOnlyList<string> SafeRepairSequence);
