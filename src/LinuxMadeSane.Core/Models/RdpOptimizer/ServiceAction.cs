using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record ServiceAction(
    ServiceActionKind Action,
    string ServiceName,
    string Reason,
    bool IsDestructive,
    string PlannedCommand);
