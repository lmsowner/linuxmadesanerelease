// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;
using System.Text.Json.Serialization;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.HomeLab;

namespace LinuxMadeSane.Application.Contracts.HomeLab;

internal static class HomeLabCatalogFileStore
{
    private const string AppsFileName = "apps.json";
    private const string RecipesFileName = "recipes.json";
    private const string PromptRecipesFileName = "prompt-recipes.json";
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static string? userCatalogRoot;
    private static string? defaultCatalogRoot;
    private static FileCache<HomeLabAppManifest>? appsCache;
    private static FileCache<HomeLabRecipeManifest>? recipesCache;
    private static FileCache<PublishedHomeLabPromptRecipe>? promptRecipesCache;

    public static bool IsConfigured => userCatalogRoot is not null;

    public static void Configure(string configuredUserCatalogRoot, string configuredDefaultCatalogRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredUserCatalogRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredDefaultCatalogRoot);

        lock (Sync)
        {
            userCatalogRoot = Path.GetFullPath(configuredUserCatalogRoot);
            defaultCatalogRoot = Path.GetFullPath(configuredDefaultCatalogRoot);
            Directory.CreateDirectory(userCatalogRoot);
            SeedMissingDefaults();
            appsCache = null;
            recipesCache = null;
            promptRecipesCache = null;
        }
    }

    public static IReadOnlyList<HomeLabAppManifest> LoadApps(IReadOnlyList<HomeLabAppManifest> builtIns)
    {
        lock (Sync)
        {
            var custom = LoadFile<AppCatalogDocument, HomeLabAppManifest>(
                AppsFileName,
                document => document.Apps,
                static app => app.Id,
                NormalizeApp,
                ref appsCache);

            return MergeById(builtIns, custom, static app => app.Id);
        }
    }

    public static IReadOnlyList<HomeLabRecipeManifest> LoadRecipes(IReadOnlyList<HomeLabRecipeManifest> builtIns)
    {
        lock (Sync)
        {
            var custom = LoadFile<RecipeCatalogDocument, HomeLabRecipeManifest>(
                RecipesFileName,
                document => document.Recipes,
                static recipe => recipe.Id,
                static recipe => recipe with
                {
                    AppIds = recipe.AppIds ?? [],
                    SharedStorageRoles = recipe.SharedStorageRoles ?? [],
                    Relationships = recipe.Relationships ?? [],
                    ConnectivityDependencies = recipe.ConnectivityDependencies ?? []
                },
                ref recipesCache);

            return MergeById(builtIns, custom, static recipe => recipe.Id);
        }
    }

    public static IReadOnlyList<HomeLabPromptRecipe> LoadPromptRecipes(
        IReadOnlyList<HomeLabPromptRecipe> builtIns,
        Func<PublishedHomeLabPromptRecipe, HomeLabPromptRecipe?> converter)
    {
        lock (Sync)
        {
            var definitions = LoadFile<PromptRecipeCatalogDocument, PublishedHomeLabPromptRecipe>(
                PromptRecipesFileName,
                document => document.Recipes,
                static recipe => recipe.Id,
                static recipe => recipe,
                ref promptRecipesCache);
            var custom = definitions.Select(converter).OfType<HomeLabPromptRecipe>().ToArray();
            return MergeById(builtIns, custom, static recipe => recipe.Id);
        }
    }

    private static IReadOnlyList<T> LoadFile<TDocument, T>(
        string fileName,
        Func<TDocument, IReadOnlyList<T>?> getItems,
        Func<T, string> getId,
        Func<T, T> normalize,
        ref FileCache<T>? cache)
    {
        var path = GetUserFilePath(fileName);
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        var fileInfo = new FileInfo(path);
        if (cache is not null && cache.LastWriteUtc == fileInfo.LastWriteTimeUtc && cache.Length == fileInfo.Length)
        {
            return cache.Items;
        }

        try
        {
            var document = JsonSerializer.Deserialize<TDocument>(File.ReadAllText(path), JsonOptions);
            var items = (document is null ? [] : getItems(document) ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(getId(item)))
                .GroupBy(getId, StringComparer.OrdinalIgnoreCase)
                .Select(group => normalize(group.Last()))
                .ToArray();
            cache = new FileCache<T>(fileInfo.LastWriteTimeUtc, fileInfo.Length, items);
            return items;
        }
        catch (JsonException)
        {
            cache = new FileCache<T>(fileInfo.LastWriteTimeUtc, fileInfo.Length, []);
            return [];
        }
        catch (IOException)
        {
            return cache?.Items ?? [];
        }
    }

    private static IReadOnlyList<T> MergeById<T>(
        IReadOnlyList<T> builtIns,
        IReadOnlyList<T> custom,
        Func<T, string> getId)
    {
        var merged = new List<T>(builtIns.Count + custom.Count);
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in builtIns.Concat(custom))
        {
            var id = getId(item).Trim();
            if (id.Length == 0)
            {
                continue;
            }

            if (positions.TryGetValue(id, out var position))
            {
                merged[position] = item;
            }
            else
            {
                positions[id] = merged.Count;
                merged.Add(item);
            }
        }

        return merged;
    }

    private static HomeLabAppManifest NormalizeApp(HomeLabAppManifest app)
    {
        var normalized = app with
        {
            Ports = app.Ports ?? [],
            Volumes = app.Volumes ?? [],
            Environment = app.Environment ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Dependencies = app.Dependencies ?? [],
            ConfigurationSchema = app.ConfigurationSchema ?? [],
            DockerCapabilities = app.DockerCapabilities ?? [],
            DockerDevices = app.DockerDevices ?? []
        };

        // Existing user catalogs were seeded before the Squid manifest had a
        // valid image health check or a LAN listener. Migrate that shipped
        // definition in memory while preserving every other user edit.
        if (!normalized.Id.Equals("squid-proxy", StringComparison.OrdinalIgnoreCase) ||
            !normalized.DefinitionVersion.Equals("1", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        var ports = normalized.Ports
            .Select(port => port.Name.Equals("proxy", StringComparison.OrdinalIgnoreCase)
                ? port with { HostBindingAddress = "0.0.0.0" }
                : port)
            .ToArray();
        var exposure = normalized.Exposure ?? new HomeLabServiceExposureManifest(
            [HomeLabEndpointScope.Internal, HomeLabEndpointScope.Lan, HomeLabEndpointScope.Client],
            new HomeLabClientAccessManifest(true, "proxy"));

        return normalized with
        {
            DefinitionVersion = "2",
            Ports = ports,
            HealthCheck = new HomeLabHealthCheckManifest(
                DockerCommand: "test -s /run/squid.pid && kill -0 \"$(cat /run/squid.pid)\"",
                StartPeriodSeconds: 30),
            Exposure = exposure
        };
    }

    private static void SeedMissingDefaults()
    {
        if (defaultCatalogRoot is null || userCatalogRoot is null || !Directory.Exists(defaultCatalogRoot))
        {
            return;
        }

        foreach (var fileName in new[] { AppsFileName, RecipesFileName, PromptRecipesFileName })
        {
            var source = Path.Combine(defaultCatalogRoot, fileName);
            var destination = Path.Combine(userCatalogRoot, fileName);
            if (File.Exists(source) && !File.Exists(destination))
            {
                File.Copy(source, destination);
            }
        }
    }

    private static string? GetUserFilePath(string fileName) =>
        userCatalogRoot is null ? null : Path.Combine(userCatalogRoot, fileName);

    private static JsonSerializerOptions CreateJsonOptions() =>
        new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

    private sealed record AppCatalogDocument(int SchemaVersion, IReadOnlyList<HomeLabAppManifest>? Apps);

    private sealed record RecipeCatalogDocument(int SchemaVersion, IReadOnlyList<HomeLabRecipeManifest>? Recipes);

    private sealed record PromptRecipeCatalogDocument(int SchemaVersion, IReadOnlyList<PublishedHomeLabPromptRecipe>? Recipes);

    private sealed record FileCache<T>(DateTime LastWriteUtc, long Length, IReadOnlyList<T> Items);
}
