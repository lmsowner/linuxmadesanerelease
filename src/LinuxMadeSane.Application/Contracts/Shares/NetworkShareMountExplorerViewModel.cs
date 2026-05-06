using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record NetworkShareMountExplorerViewModel(
    ShareToolingStatusViewModel Tooling,
    NetworkShareMachineDiscoveryResult Discovery,
    IReadOnlyList<CurrentSystemMount> CurrentMounts,
    IReadOnlyList<ManagedRemoteShareMount> ManagedMounts);
