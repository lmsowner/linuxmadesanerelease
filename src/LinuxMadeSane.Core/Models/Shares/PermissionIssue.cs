// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record PermissionIssue(
    PermissionIssueSeverity Severity,
    string Title,
    string Detail,
    string SuggestedFix,
    string RiskNote);
