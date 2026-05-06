using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record PermissionIssue(
    PermissionIssueSeverity Severity,
    string Title,
    string Detail,
    string SuggestedFix,
    string RiskNote);
