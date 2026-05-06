using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record SambaShareSystemCheckResult(
    DateTimeOffset CheckedAtUtc,
    string Summary,
    bool IsHealthy,
    IReadOnlyList<SambaShareServiceCheck> Checks,
    IReadOnlyList<ShareSystemCheckFinding> Findings,
    IReadOnlyList<SambaShareRuntimeStatus> ShareStatuses);

public sealed record SambaShareServiceCheck(
    string Key,
    string Label,
    bool IsHealthy,
    PermissionIssueSeverity Severity,
    string Status,
    string Detail);

public sealed record ShareSystemCheckFinding(
    PermissionIssueSeverity Severity,
    string Title,
    string Detail,
    string Recommendation);

public sealed record SambaShareRuntimeStatus(
    Guid ShareId,
    string ShareName,
    string SharePath,
    bool IsHealthy,
    PermissionIssueSeverity Severity,
    string Status,
    string Detail,
    IReadOnlyList<ShareSystemCheckFinding> Findings);
