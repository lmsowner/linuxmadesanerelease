using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISftpServerStore
{
    Task<SftpHostSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(SftpHostSettings settings, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SftpManagedUser>> ListUsersAsync(CancellationToken cancellationToken = default);

    Task<SftpManagedUser?> GetUserAsync(string userName, CancellationToken cancellationToken = default);

    Task SaveUserAsync(SftpManagedUser user, CancellationToken cancellationToken = default);

    Task DeleteUserAsync(string userName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SftpAuditEntry>> ListAuditEntriesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SftpAuditEntry>> ListAuditEntriesAsync(string userName, CancellationToken cancellationToken = default);

    Task SaveAuditEntryAsync(SftpAuditEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SftpBackupSnapshot>> ListBackupSnapshotsAsync(CancellationToken cancellationToken = default);

    Task<SftpBackupSnapshot?> GetBackupSnapshotAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveBackupSnapshotAsync(SftpBackupSnapshot snapshot, CancellationToken cancellationToken = default);
}
