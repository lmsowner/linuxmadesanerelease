// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.HomeLab;

namespace LinuxMadeSane.Application.Interfaces;

public interface IHomeLabPromptRecipeProvider
{
    Task<IReadOnlyList<HomeLabPromptRecipe>> GetRecipesAsync(CancellationToken cancellationToken = default);
    Task<HomeLabPromptRecipe> GetRecipeAsync(string id, CancellationToken cancellationToken = default);
}
