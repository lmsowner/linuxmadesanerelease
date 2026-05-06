using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record OwnershipWizardPreset(
    SharePresetType PresetType,
    string Name,
    string Summary,
    string Outcome,
    IReadOnlyList<string> RecommendedSteps);
