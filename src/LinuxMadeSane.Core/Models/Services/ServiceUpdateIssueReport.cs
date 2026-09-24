// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.Services;

public sealed record ServiceUpdateIssueReport(
    Guid ServiceId,
    string UnitName,
    IReadOnlyList<ServiceRepairIssue> Issues,
    IReadOnlyList<string> OneClickFixCandidates);
