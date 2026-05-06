using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Core.Abstractions;

public interface IAiAuditService
{
    Task RecordAsync(AiAuditEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiAuditEntry>> ListAsync(Guid threadId, CancellationToken cancellationToken = default);
}
