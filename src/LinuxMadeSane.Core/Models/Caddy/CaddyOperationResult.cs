using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Core.Models.Caddy;

public sealed record CaddyOperationResult(
    bool Success,
    string Summary,
    IReadOnlyList<OperationLogEntry> Logs);
