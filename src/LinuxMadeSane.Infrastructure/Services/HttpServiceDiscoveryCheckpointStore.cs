// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;

namespace LinuxMadeSane.Infrastructure.Services;

internal sealed class HttpServiceDiscoveryCheckpointStore(HttpServiceDiscoveryStorageSettings settings)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(settings.ScanCheckpointsPath))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            await using var stream = File.OpenRead(settings.ScanCheckpointsPath);
            var document = await JsonSerializer.DeserializeAsync<HttpServiceDiscoveryCheckpointDocument>(
                stream,
                JsonOptions,
                cancellationToken);
            return new Dictionary<string, string>(
                document?.CompletedTargets ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task RecordAsync(
        string targetKey,
        string inventoryFingerprint,
        CancellationToken cancellationToken = default)
    {
        var completed = new Dictionary<string, string>(await ReadAsync(cancellationToken), StringComparer.OrdinalIgnoreCase)
        {
            [targetKey] = inventoryFingerprint
        };
        await WriteAsync(completed, cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(settings.ScanCheckpointsPath);
        return Task.CompletedTask;
    }

    internal static bool IsComplete(
        IReadOnlyDictionary<string, string> completed,
        string targetKey,
        string inventoryFingerprint) =>
        completed.TryGetValue(targetKey, out var recordedFingerprint) &&
        recordedFingerprint.Equals(inventoryFingerprint, StringComparison.Ordinal);

    private async Task WriteAsync(
        IReadOnlyDictionary<string, string> completed,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(settings.RootDirectory);
        var document = new HttpServiceDiscoveryCheckpointDocument(
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>(completed, StringComparer.OrdinalIgnoreCase));
        var temporaryPath = $"{settings.ScanCheckpointsPath}.{Guid.NewGuid():N}.tmp";
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

            File.Move(temporaryPath, settings.ScanCheckpointsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record HttpServiceDiscoveryCheckpointDocument(
        DateTimeOffset UpdatedAtUtc,
        IReadOnlyDictionary<string, string> CompletedTargets);
}
