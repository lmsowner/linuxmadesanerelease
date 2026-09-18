// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Infrastructure.Services;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class JsonOnDemandAppFavouriteStore(HttpServiceDiscoveryStorageSettings storageSettings)
    : IOnDemandAppFavouriteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path = Path.Combine(storageSettings.RootDirectory, "on-demand-favourites.json");

    public async Task<IReadOnlySet<string>> GetAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = NormalizeUserId(userId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var favourites = await ReadAsync(cancellationToken);
            return favourites.TryGetValue(normalizedUserId, out var serviceKeys)
                ? serviceKeys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SetAsync(
        string userId,
        string serviceKey,
        bool isFavourite,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = NormalizeUserId(userId);
        var normalizedServiceKey = NormalizeServiceKey(serviceKey);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var favourites = await ReadAsync(cancellationToken);
            if (!favourites.TryGetValue(normalizedUserId, out var serviceKeys))
            {
                serviceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                favourites[normalizedUserId] = serviceKeys;
            }

            if (isFavourite)
            {
                serviceKeys.Add(normalizedServiceKey);
            }
            else
            {
                serviceKeys.Remove(normalizedServiceKey);
                if (serviceKeys.Count == 0)
                {
                    favourites.Remove(normalizedUserId);
                }
            }

            await WriteAsync(favourites, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Dictionary<string, HashSet<string>>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }

        await using var stream = File.OpenRead(path);
        var stored = await JsonSerializer.DeserializeAsync<Dictionary<string, HashSet<string>>>(
            stream,
            JsonOptions,
            cancellationToken);
        return stored is null
            ? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, HashSet<string>>(stored, StringComparer.OrdinalIgnoreCase);
    }

    private async Task WriteAsync(
        Dictionary<string, HashSet<string>> favourites,
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
                await JsonSerializer.SerializeAsync(stream, favourites, JsonOptions, cancellationToken);
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
}
