using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record PackageAction(
    PackageActionKind Action,
    string PackageName,
    string Reason,
    bool IsDestructive,
    string PlannedCommand);
