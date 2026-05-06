namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record ServiceState(
    string Name,
    bool IsEnabled,
    bool IsActive,
    bool IsMasked,
    string UnitFileState,
    string ActiveState,
    string Description);
