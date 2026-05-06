using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record MountHelperViewModel(
    SambaShareDefinition Share,
    IReadOnlyList<MountRecipe> Recipes);
