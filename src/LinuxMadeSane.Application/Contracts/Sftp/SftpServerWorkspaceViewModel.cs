using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Application.Contracts.Sftp;

public sealed record SftpServerWorkspaceViewModel(
    SftpHostConfiguration Overview,
    IReadOnlyList<SftpManagedUser> Users,
    IReadOnlyList<SftpAuditEntry> AuditEntries,
    IReadOnlyList<SftpBackupSnapshot> BackupSnapshots);
