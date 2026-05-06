using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record ShareToolingInstallResult(
    bool Success,
    string HostName,
    string StatusMessage,
    IReadOnlyList<string> RequestedPackageNames,
    IReadOnlyList<OperationLogEntry> OperationLogs);
