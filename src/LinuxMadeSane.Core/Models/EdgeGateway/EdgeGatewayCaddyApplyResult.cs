using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Core.Models.EdgeGateway;

public sealed record EdgeGatewayCaddyApplyResult(
    bool Success,
    string Summary,
    string GeneratedConfigPath,
    IReadOnlyList<OperationLogEntry> Logs);
