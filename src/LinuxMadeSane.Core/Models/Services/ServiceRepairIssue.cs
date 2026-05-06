using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Services;

public sealed record ServiceRepairIssue(
    RepairRiskLevel RiskLevel,
    ServiceDiagnosticSeverity Severity,
    string Title,
    string Detail,
    string WhyItBreaks,
    string RecommendedRepair);
