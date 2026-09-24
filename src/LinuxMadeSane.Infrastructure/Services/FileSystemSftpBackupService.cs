// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class FileSystemSftpBackupService(
    ISftpServerStore store,
    ILinuxCommandRunner commandRunner,
    SftpBackupStorageSettings storageSettings) : ISftpBackupService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public async Task<SftpBackupSnapshot> CreateSnapshotAsync(
        string summary,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(storageSettings.SnapshotsDirectory);

        var snapshotId = Guid.NewGuid();
        var createdAtUtc = DateTimeOffset.UtcNow;
        var snapshotDirectory = Path.Combine(
            storageSettings.SnapshotsDirectory,
            $"{createdAtUtc:yyyyMMddHHmmss}-{snapshotId:N}");
        Directory.CreateDirectory(snapshotDirectory);

        var files = new List<SftpBackupFile>();
        var index = 0;
        foreach (var path in filePaths
                     .Where(static path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existed = File.Exists(path);
            var backupPath = existed
                ? Path.Combine(snapshotDirectory, $"{index:D2}-{Path.GetFileName(path)}")
                : string.Empty;

            if (existed)
            {
                var backup = await commandRunner.RunAsync(
                    new LinuxCommandRequest(
                        "install",
                        ["-D", "-m", "600", path, backupPath],
                        true,
                        CommandTimeout,
                        $"Back up SFTP file {path}"),
                    dryRun: false,
                    cancellationToken);

                if (backup.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Linux Made Sane could not back up '{path}': {SftpSystemCommandHelper.BuildFailureDetail(backup)}");
                }
            }

            files.Add(new SftpBackupFile(path, backupPath, existed));
            index++;
        }

        var snapshot = new SftpBackupSnapshot(snapshotId, summary, files, createdAtUtc, true, snapshotDirectory);
        await store.SaveBackupSnapshotAsync(snapshot, cancellationToken);
        return snapshot;
    }

    public async Task RestoreAsync(SftpBackupSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        foreach (var file in snapshot.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (file.ExistedBeforeSnapshot)
            {
                if (!File.Exists(file.BackupPath))
                {
                    continue;
                }

                var restore = await commandRunner.RunAsync(
                    new LinuxCommandRequest(
                        "install",
                        ["-D", "-m", "600", file.BackupPath, file.SourcePath],
                        true,
                        CommandTimeout,
                        $"Restore SFTP file {file.SourcePath}"),
                    dryRun: false,
                    cancellationToken);

                if (restore.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Linux Made Sane could not restore '{file.SourcePath}': {SftpSystemCommandHelper.BuildFailureDetail(restore)}");
                }

                continue;
            }

            if (File.Exists(file.SourcePath))
            {
                var remove = await commandRunner.RunAsync(
                    new LinuxCommandRequest(
                        "rm",
                        ["-f", file.SourcePath],
                        true,
                        CommandTimeout,
                        $"Remove SFTP file {file.SourcePath} during rollback"),
                    dryRun: false,
                    cancellationToken);

                if (remove.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Linux Made Sane could not remove '{file.SourcePath}' during rollback: {SftpSystemCommandHelper.BuildFailureDetail(remove)}");
                }
            }
        }
    }
}
