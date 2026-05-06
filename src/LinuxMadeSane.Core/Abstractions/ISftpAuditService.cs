using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISftpAuditService
{
    Task RecordAsync(SftpAuditEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SftpAuditEntry>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SftpAuditEntry>> ListForUserAsync(string userName, CancellationToken cancellationToken = default);
}
