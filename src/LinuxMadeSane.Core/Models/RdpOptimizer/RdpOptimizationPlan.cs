using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record RdpOptimizationPlan(
    Guid PlanId,
    DateTimeOffset CreatedAt,
    RdpOptimizationProfile Profile,
    bool DryRun,
    bool InspectOnly,
    DesktopInspectionReport Inspection,
    IReadOnlyList<PackageAction> PackageActions,
    IReadOnlyList<ServiceAction> ServiceActions,
    IReadOnlyList<SessionConfigurationChange> SessionChanges,
    IReadOnlyList<string> AutostartActions,
    IReadOnlyList<string> Warnings,
    bool HasDestructiveChanges);
