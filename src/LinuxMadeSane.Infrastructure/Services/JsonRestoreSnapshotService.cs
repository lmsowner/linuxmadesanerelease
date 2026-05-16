// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class JsonRestoreSnapshotService(RdpOptimizerStorageSettings settings) : IRestoreSnapshotService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public async Task<RestoreSnapshot> CreateSnapshotAsync(
        RdpOptimizationProfile profile,
        DesktopInspectionReport inspection,
        IReadOnlyList<string> filesToBackup,
        CancellationToken cancellationToken = default)
    {
        var snapshotId = Guid.NewGuid();
        var snapshotDirectory = Path.Combine(settings.SnapshotsDirectory, snapshotId.ToString("N"));
        var filesDirectory = Path.Combine(snapshotDirectory, "files");

        Directory.CreateDirectory(filesDirectory);

        var fileBackups = new List<SessionFileBackup>();
        foreach (var sourcePath in filesToBackup.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = sourcePath.TrimStart(Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var backupPath = Path.Combine(filesDirectory, relativePath);
            var existed = File.Exists(sourcePath);

            if (existed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(sourcePath, backupPath, overwrite: true);
            }

            fileBackups.Add(new SessionFileBackup(sourcePath, backupPath, existed));
        }

        var snapshot = new RestoreSnapshot(
            snapshotId,
            DateTimeOffset.UtcNow,
            profile,
            inspection.UbuntuVersion,
            inspection.DisplayManager,
            inspection.Packages,
            inspection.Services,
            inspection.SessionConfiguration,
            fileBackups,
            Array.Empty<string>(),
            ["Snapshot created by Linux Made Sane before desktop mode changes."]);

        await WriteSnapshotAsync(snapshot, cancellationToken);
        return snapshot;
    }

    public Task UpdateSnapshotAsync(RestoreSnapshot snapshot, CancellationToken cancellationToken = default) =>
        WriteSnapshotAsync(snapshot, cancellationToken);

    public async Task<RestoreSnapshot?> GetSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default)
    {
        var path = GetSnapshotPath(snapshotId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<RestoreSnapshot>(stream, SerializerOptions, cancellationToken);
    }

    public async Task<IReadOnlyList<RestoreSnapshot>> ListSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(settings.SnapshotsDirectory);

        var snapshots = new List<RestoreSnapshot>();
        foreach (var file in Directory.EnumerateFiles(settings.SnapshotsDirectory, "snapshot.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var stream = File.OpenRead(file);
            var snapshot = await JsonSerializer.DeserializeAsync<RestoreSnapshot>(stream, SerializerOptions, cancellationToken);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    public async Task SaveRunResultAsync(RdpOptimizationResult result, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(settings.RunsDirectory);
        var path = Path.Combine(settings.RunsDirectory, $"{result.StartedAt:yyyyMMddHHmmss}-{result.RunId:N}.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, result, SerializerOptions, cancellationToken);
    }

    public async Task<IReadOnlyList<RdpOptimizationResult>> ListRunResultsAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(settings.RunsDirectory);

        var results = new List<RdpOptimizationResult>();
        foreach (var file in Directory.EnumerateFiles(settings.RunsDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var stream = File.OpenRead(file);
            var result = await JsonSerializer.DeserializeAsync<RdpOptimizationResult>(stream, SerializerOptions, cancellationToken);
            if (result is not null)
            {
                results.Add(result);
            }
        }

        return results.OrderByDescending(item => item.StartedAt).ToArray();
    }

    private async Task WriteSnapshotAsync(RestoreSnapshot snapshot, CancellationToken cancellationToken)
    {
        var path = GetSnapshotPath(snapshot.SnapshotId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, snapshot, SerializerOptions, cancellationToken);
    }

    private string GetSnapshotPath(Guid snapshotId) =>
        Path.Combine(settings.SnapshotsDirectory, snapshotId.ToString("N"), "snapshot.json");
}
