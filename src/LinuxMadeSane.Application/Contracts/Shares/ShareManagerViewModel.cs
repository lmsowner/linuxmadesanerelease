using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record ShareManagerViewModel(
    IReadOnlyList<SambaShareDefinition> Shares,
    SambaShareSystemCheckResult? SystemCheck = null);
