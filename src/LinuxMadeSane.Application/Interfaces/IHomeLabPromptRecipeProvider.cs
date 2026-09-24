// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.HomeLab;

namespace LinuxMadeSane.Application.Interfaces;

public interface IHomeLabPromptRecipeProvider
{
    Task<IReadOnlyList<HomeLabPromptRecipe>> GetRecipesAsync(CancellationToken cancellationToken = default);
    Task<HomeLabPromptRecipe> GetRecipeAsync(string id, CancellationToken cancellationToken = default);
}
