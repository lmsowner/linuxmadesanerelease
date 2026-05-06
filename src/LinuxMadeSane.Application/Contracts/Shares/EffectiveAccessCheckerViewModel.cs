using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record EffectiveAccessCheckerViewModel(
    IReadOnlyList<SambaShareDefinition> AvailableShares,
    EffectiveAccessCheckResult Result);
