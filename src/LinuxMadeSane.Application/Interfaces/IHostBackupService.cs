// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Infrastructure;
namespace LinuxMadeSane.Application.Interfaces;
public interface IHostBackupService
{
    Task<BackupWorkspace> GetAsync(CancellationToken token = default);
    Task SaveRepositoryAsync(BackupRepository repository, string? password, bool initialize, CancellationToken token = default);
    Task<string> InspectFullSystemAsync(Guid repositoryId, CancellationToken token = default);
    Task SaveSetAsync(BackupSet set, bool schedule, int hour, int minute, CancellationToken token = default);
    Task RecordRecoveryTestAsync(Guid setId, string snapshotId, string evidence, CancellationToken token = default);
    Task StartBackupAsync(Guid setId, CancellationToken token = default);
    Task<IReadOnlyList<BackupProgress>> GetProgressAsync(CancellationToken token = default);
    Task RunBackupAsync(Guid setId, CancellationToken token = default);
    Task<IReadOnlyList<BackupSnapshot>> SnapshotsAsync(Guid repositoryId, CancellationToken token = default);
    Task<IReadOnlyList<BackupFile>> FilesAsync(Guid repositoryId, string snapshot, CancellationToken token = default);
    Task<string> InspectRepositoryAsync(Guid repositoryId, bool check, CancellationToken token = default);
    Task RestoreAsync(Guid repositoryId, string snapshot, string target, IReadOnlyList<string> include,
        bool overwriteConfirmed, CancellationToken token = default);
    Task<string> ValidateLmsRestoreAsync(string restoredDirectory, CancellationToken token = default);
    Task<string> PrepareLmsRestoreAsync(string restoredDirectory, string preparationDirectory,
        string targetDataDirectory, string targetApplicationDirectory, string targetKeyDirectory, CancellationToken token = default);
}
