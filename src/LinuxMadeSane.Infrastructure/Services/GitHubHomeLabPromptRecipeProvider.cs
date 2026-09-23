// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net.Http.Json;
using LinuxMadeSane.Application.Contracts.HomeLab;
using LinuxMadeSane.Application.Interfaces;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class GitHubHomeLabPromptRecipeProvider(HttpClient httpClient) : IHomeLabPromptRecipeProvider
{
    public const string CatalogUrl = "https://raw.githubusercontent.com/lmsowner/linuxmadesanerelease/main/catalog/home-lab-prompt-recipes.json";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private IReadOnlyList<HomeLabPromptRecipe> cachedRecipes = HomeLabPromptRecipeCatalog.All;
    private DateTimeOffset refreshAfterUtc = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<HomeLabPromptRecipe>> GetRecipesAsync(CancellationToken cancellationToken = default)
    {
        if (DateTimeOffset.UtcNow < refreshAfterUtc)
        {
            return cachedRecipes;
        }

        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow < refreshAfterUtc)
            {
                return cachedRecipes;
            }

            try
            {
                var catalog = await httpClient.GetFromJsonAsync<PublishedHomeLabPromptRecipeCatalog>(CatalogUrl, cancellationToken);
                if (catalog?.SchemaVersion is not (1 or 2))
                {
                    throw new InvalidOperationException("The public HomeLab Recipe catalog uses an unsupported schema version.");
                }
                var recipes = (catalog?.Recipes ?? [])
                    .Take(100)
                    .Select(TryCreateRecipe)
                    .OfType<HomeLabPromptRecipe>()
                    .GroupBy(recipe => recipe.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToArray();
                if (recipes.Length == 0)
                {
                    throw new InvalidOperationException("The public HomeLab Recipe catalog did not contain any compatible recipes.");
                }

                cachedRecipes = recipes;
                refreshAfterUtc = DateTimeOffset.UtcNow.Add(RefreshInterval);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                refreshAfterUtc = DateTimeOffset.UtcNow.Add(FailureRetryInterval);
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
            {
                refreshAfterUtc = DateTimeOffset.UtcNow.Add(FailureRetryInterval);
            }

            return cachedRecipes;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public async Task<HomeLabPromptRecipe> GetRecipeAsync(string id, CancellationToken cancellationToken = default) =>
        (await GetRecipesAsync(cancellationToken))
        .FirstOrDefault(recipe => recipe.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"HomeLab Recipe '{id}' is not available from the public catalog or packaged fallback.");

    private static HomeLabPromptRecipe? TryCreateRecipe(PublishedHomeLabPromptRecipe definition)
    {
        try
        {
            return HomeLabPromptRecipeCatalog.CreatePublished(definition);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
