using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record SshfsMountExplorerViewModel(
    SshfsToolingStatusViewModel Tooling,
    IReadOnlyList<SshfsMountHostCandidate> HostCandidates,
    IReadOnlyList<CurrentSystemMount> CurrentMounts,
    IReadOnlyList<ManagedSshfsMount> ManagedMounts);
