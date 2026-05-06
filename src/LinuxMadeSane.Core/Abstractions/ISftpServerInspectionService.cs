using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISftpServerInspectionService
{
    Task<SftpHostConfiguration> InspectAsync(CancellationToken cancellationToken = default);
}
