using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Contracts.Caddy;

public sealed record CaddyOperationResultViewModel(
    bool Success,
    string Summary,
    IReadOnlyList<OperationLogEntry> Logs);
