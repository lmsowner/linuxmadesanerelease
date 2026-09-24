// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;
using System.Text.Json.Serialization;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Cloudflare;
using LinuxMadeSane.Infrastructure.Services;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class JsonOnDemandAppFavouriteStore(HttpServiceDiscoveryStorageSettings storageSettings)
    : IOnDemandAppFavouriteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path = Path.Combine(storageSettings.RootDirectory, "on-demand-favourites.json");

    public async Task<IReadOnlyList<OnDemandAppFavourite>> ListAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = NormalizeUserId(userId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadAsync(cancellationToken);
            return document.Users.TryGetValue(normalizedUserId, out var favourites)
                ? favourites.OrderByDescending(item => item.UpdatedAtUtc).ToArray()
                : [];
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<OnDemandAppFavourite>> ListAllAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return (await ReadAsync(cancellationToken)).Users.Values
                .SelectMany(static favourites => favourites)
                .GroupBy(static item => item.ServiceKey, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.OrderByDescending(item => item.UpdatedAtUtc).First())
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(
        string userId,
        OnDemandAppFavourite favourite,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(favourite);
        var normalizedUserId = NormalizeUserId(userId);
        var normalizedServiceKey = NormalizeServiceKey(favourite.ServiceKey);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadAsync(cancellationToken);
            if (!document.Users.TryGetValue(normalizedUserId, out var favourites))
            {
                favourites = [];
                document.Users[normalizedUserId] = favourites;
            }

            favourites.RemoveAll(item => item.ServiceKey.Equals(normalizedServiceKey, StringComparison.OrdinalIgnoreCase));
            favourites.Add(favourite with { ServiceKey = normalizedServiceKey });
            await WriteAsync(document, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RemoveAsync(
        string userId,
        string serviceKey,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = NormalizeUserId(userId);
        var normalizedServiceKey = NormalizeServiceKey(serviceKey);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadAsync(cancellationToken);
            if (document.Users.TryGetValue(normalizedUserId, out var favourites))
            {
                favourites.RemoveAll(item => item.ServiceKey.Equals(normalizedServiceKey, StringComparison.OrdinalIgnoreCase));
                if (favourites.Count == 0)
                {
                    document.Users.Remove(normalizedUserId);
                }

                await WriteAsync(document, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RefreshEndpointsAsync(
        IReadOnlyList<LocalHttpServiceEndpoint> endpoints,
        CancellationToken cancellationToken = default)
    {
        var currentByKey = endpoints
            .GroupBy(LocalHttpServiceDiscoveryRanking.StableKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (currentByKey.Count == 0)
        {
            return;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadAsync(cancellationToken);
            var changed = false;
            foreach (var favourites in document.Users.Values)
            {
                for (var index = 0; index < favourites.Count; index++)
                {
                    var favourite = favourites[index];
                    if (!currentByKey.TryGetValue(favourite.ServiceKey, out var current) || favourite.Endpoint == current)
                    {
                        continue;
                    }

                    favourites[index] = favourite with { Endpoint = current };
                    changed = true;
                }
            }

            if (changed)
            {
                await WriteAsync(document, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<StoredFavouritesDocument> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return StoredFavouritesDocument.Empty();
        }

        await using var stream = File.OpenRead(path);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (json.RootElement.TryGetProperty("users", out _))
        {
            return json.RootElement.Deserialize<StoredFavouritesDocument>(JsonOptions) ?? StoredFavouritesDocument.Empty();
        }

        var legacy = json.RootElement.Deserialize<Dictionary<string, HashSet<string>>>(JsonOptions) ?? [];
        var users = new Dictionary<string, List<OnDemandAppFavourite>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (userId, serviceKeys) in legacy)
        {
            users[userId] = serviceKeys.Select(serviceKey => new OnDemandAppFavourite(
                    NormalizeServiceKey(serviceKey),
                    Endpoint: null,
                    new OnDemandAppProxyPreferences(),
                    DateTimeOffset.MinValue))
                .ToList();
        }

        return new StoredFavouritesDocument(2, users);
    }

    private async Task WriteAsync(
        StoredFavouritesDocument document,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(storageSettings.RootDirectory);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16_384,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string NormalizeUserId(string userId) =>
        string.IsNullOrWhiteSpace(userId)
            ? throw new ArgumentException("A user ID is required.", nameof(userId))
            : userId.Trim().ToLowerInvariant();

    private static string NormalizeServiceKey(string serviceKey) =>
        string.IsNullOrWhiteSpace(serviceKey)
            ? throw new ArgumentException("A service key is required.", nameof(serviceKey))
            : serviceKey.Trim().ToLowerInvariant();

    private sealed record StoredFavouritesDocument(
        int Version,
        Dictionary<string, List<OnDemandAppFavourite>> Users)
    {
        public static StoredFavouritesDocument Empty() =>
            new(2, new Dictionary<string, List<OnDemandAppFavourite>>(StringComparer.OrdinalIgnoreCase));
    }
}
