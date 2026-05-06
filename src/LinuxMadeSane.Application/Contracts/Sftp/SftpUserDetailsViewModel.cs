using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Application.Contracts.Sftp;

public sealed record SftpUserDetailsViewModel(
    SftpManagedUser User,
    IReadOnlyList<SftpAuditEntry> AuditEntries);
