// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Services;

public sealed record ServiceUpdateIssueReport(
    Guid ServiceId,
    string UnitName,
    IReadOnlyList<ServiceRepairIssue> Issues,
    IReadOnlyList<string> OneClickFixCandidates);
