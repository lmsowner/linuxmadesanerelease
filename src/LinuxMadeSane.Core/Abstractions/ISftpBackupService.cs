using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISftpBackupService
{
    Task<SftpBackupSnapshot> CreateSnapshotAsync(
        string summary,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default);

    Task RestoreAsync(SftpBackupSnapshot snapshot, CancellationToken cancellationToken = default);
}
