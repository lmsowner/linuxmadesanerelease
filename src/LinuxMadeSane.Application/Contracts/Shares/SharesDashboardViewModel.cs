using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record SharesDashboardViewModel(
    IReadOnlyList<SambaShareDefinition> Shares,
    IReadOnlyList<OwnershipWizardPreset> Presets,
    IReadOnlyList<PermissionIssue> HighlightedIssues);
