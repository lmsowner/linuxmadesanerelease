// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Core.Models.LocalAi;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalAiPeerSharingStore(LocalAiPeerSharingStorageSettings storageSettings)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<LocalAiPeerSharingStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var settings = await ReadAsync(cancellationToken);
            return MapStatus(settings);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<LocalAiPeerSharingKeyResult> EnableWithNewKeyAsync(CancellationToken cancellationToken = default)
    {
        var accessKey = $"lmsai_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
        var now = DateTimeOffset.UtcNow;
        var settings = new PersistedPeerSharingSettings(true, Hash(accessKey), now, now);

        await gate.WaitAsync(cancellationToken);
        try
        {
            await WriteAsync(settings, cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        return new LocalAiPeerSharingKeyResult(MapStatus(settings), accessKey);
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadAsync(cancellationToken);
            await WriteAsync(current with { Enabled = false, UpdatedAtUtc = DateTimeOffset.UtcNow }, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> ValidateAccessKeyAsync(string? accessKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessKey))
        {
            return false;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var settings = await ReadAsync(cancellationToken);
            if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.AccessKeyHash))
            {
                return false;
            }

            var supplied = Convert.FromHexString(Hash(accessKey.Trim()));
            var expected = Convert.FromHexString(settings.AccessKeyHash);
            return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PersistedPeerSharingSettings> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(storageSettings.SettingsPath))
        {
            return PersistedPeerSharingSettings.Empty;
        }

        try
        {
            await using var stream = File.OpenRead(storageSettings.SettingsPath);
            return await JsonSerializer.DeserializeAsync<PersistedPeerSharingSettings>(stream, JsonOptions, cancellationToken)
                   ?? PersistedPeerSharingSettings.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PersistedPeerSharingSettings.Empty;
        }
    }

    private async Task WriteAsync(PersistedPeerSharingSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(storageSettings.RootDirectory);
        var temporaryPath = $"{storageSettings.SettingsPath}.{Guid.NewGuid():N}.tmp";
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
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, storageSettings.SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static LocalAiPeerSharingStatus MapStatus(PersistedPeerSharingSettings settings) =>
        new(settings.Enabled, !string.IsNullOrWhiteSpace(settings.AccessKeyHash), settings.AccessKeyIssuedAtUtc);

    private sealed record PersistedPeerSharingSettings(
        bool Enabled,
        string AccessKeyHash,
        DateTimeOffset? AccessKeyIssuedAtUtc,
        DateTimeOffset UpdatedAtUtc)
    {
        public static PersistedPeerSharingSettings Empty { get; } = new(false, string.Empty, null, DateTimeOffset.MinValue);
    }
}
