using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record OwnershipWizardViewModel(
    IReadOnlyList<OwnershipWizardPreset> Presets);
