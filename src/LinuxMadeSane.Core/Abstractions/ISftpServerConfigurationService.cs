using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISftpServerConfigurationService
{
    Task<SftpConfigurationPlan> BuildHostPlanAsync(
        SftpHostSettings settings,
        IReadOnlyList<SftpManagedUser> users,
        bool dryRun,
        CancellationToken cancellationToken = default);

    Task<SftpApplyResult> ApplyHostPlanAsync(
        SftpHostSettings settings,
        IReadOnlyList<SftpManagedUser> users,
        bool approved,
        CancellationToken cancellationToken = default);
}
