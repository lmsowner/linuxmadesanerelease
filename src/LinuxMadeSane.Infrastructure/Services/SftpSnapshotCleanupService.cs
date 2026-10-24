// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SftpSnapshotCleanupService(ISftpServerStore store, ILinuxCommandRunner runner, ISftpAuditService audit)
    : ISftpSnapshotCleanupService
{
    public async Task<int> PurgeOlderAsync(bool approved, CancellationToken cancellationToken = default)
    {
        if (!approved) throw new InvalidOperationException("Choose Purge older snapshots to confirm deletion of older configuration backups.");
        var snapshots = (await store.ListBackupSnapshotsAsync(cancellationToken))
            .OrderByDescending(snapshot => snapshot.CreatedAtUtc).ThenBy(snapshot => snapshot.Id).ToArray();
        var keep = snapshots.FirstOrDefault(snapshot => snapshot.RollbackAvailable) ?? snapshots.FirstOrDefault();
        // Only strictly older snapshots are eligible, even if a newer reference-only entry exists.
        var older = keep is null ? [] : snapshots.Where(snapshot => snapshot.CreatedAtUtc < keep.CreatedAtUtc).ToArray();
        foreach (var snapshot in older) Validate(snapshot); // Check every path before deleting anything.
        var removed = 0;
        foreach (var snapshot in older)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Validate(snapshot);
            // A partially purged backup must never be offered as a usable rollback.
            await store.SaveBackupSnapshotAsync(snapshot with { RollbackAvailable = false }, cancellationToken);
            foreach (var file in snapshot.Files.Where(file => file.ExistedBeforeSnapshot))
                await RunAsync("rm", ["-f", "--", file.BackupPath], cancellationToken);
            if (Directory.Exists(snapshot.StorageDirectory))
                await RunAsync("rmdir", ["--", snapshot.StorageDirectory], cancellationToken);
            await store.DeleteBackupSnapshotAsync(snapshot.Id, cancellationToken);
            removed++;
            await audit.RecordAsync(new(Guid.NewGuid(), "snapshot.purge", "snapshot", snapshot.Id.ToString(),
                "Deleted an older SFTP configuration snapshot.", "The newest recovery snapshot, live configuration and user files were retained.",
                true, DateTimeOffset.UtcNow, null), cancellationToken);
        }
        return removed;
    }

    private static void Validate(SftpBackupSnapshot snapshot)
    {
        var directory = Path.GetFullPath(snapshot.StorageDirectory);
        var expectedName = $"{snapshot.CreatedAtUtc:yyyyMMddHHmmss}-{snapshot.Id:N}";
        if (Path.GetFileName(directory) != expectedName
            || Path.GetFileName(Path.GetDirectoryName(directory)) != "snapshots"
            || Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(directory))) != "sftp-backups")
            throw new InvalidOperationException("Snapshot cleanup refused an unrecognised backup location. No live configuration or user files will be deleted.");
        RejectLinksAndMounts(directory);
        var expected = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < snapshot.Files.Count; index++)
        {
            var file = snapshot.Files[index];
            if (file.SourcePath != SftpServerDefaults.MainSshdConfigPath && file.SourcePath != SftpServerDefaults.ManagedDropInPath)
                throw new InvalidOperationException("Snapshot cleanup only supports SSH configuration backups.");
            if (!file.ExistedBeforeSnapshot) continue;
            var path = Path.Combine(directory, $"{index:D2}-{Path.GetFileName(file.SourcePath)}");
            if (Path.GetFullPath(file.BackupPath) != path)
                throw new InvalidOperationException("Snapshot cleanup refused a backup path outside its registered snapshot.");
            RejectLinksAndMounts(path);
            expected.Add(path);
        }
        // Never recurse or remove unexpected contents, even within a registered snapshot.
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any(path => !expected.Contains(path) || Directory.Exists(path)))
            throw new InvalidOperationException("Snapshot contains unexpected files or folders; review it manually before purging.");
    }

    private static void RejectLinksAndMounts(string path)
    {
        var mounts = DriveInfo.GetDrives().Select(drive => Path.GetFullPath(drive.Name).TrimEnd(Path.DirectorySeparatorChar)).ToHashSet(StringComparer.Ordinal);
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                throw new InvalidOperationException("Snapshot cleanup does not follow symbolic links.");
            // The filesystem root is expected; any nested mount in the path is deliberately refused.
            if (current != Path.GetPathRoot(current) && mounts.Contains(current.TrimEnd(Path.DirectorySeparatorChar)))
                throw new InvalidOperationException("Snapshot cleanup refused a mounted path. Review these backups manually.");
        }
    }

    private async Task RunAsync(string command, string[] arguments, CancellationToken token)
    {
        var result = await runner.RunAsync(new LinuxCommandRequest(command, arguments, true, TimeSpan.FromSeconds(30), "Purge older SFTP configuration snapshot"), false, token);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("Snapshot purge could not finish: " + SftpSystemCommandHelper.BuildFailureDetail(result));
    }
}
