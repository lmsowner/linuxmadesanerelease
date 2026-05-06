using LinuxMadeSane.Application.Contracts.RdpOptimizer;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Interfaces;

public interface IRdpOptimizationService
{
    Task<RdpOptimizerWorkspaceViewModel> GetWorkspaceAsync(CancellationToken cancellationToken = default);

    Task<RdpOptimizerOverviewViewModel> GetOverviewAsync(CancellationToken cancellationToken = default);

    Task<RdpOptimizerInspectViewModel> GetInspectAsync(CancellationToken cancellationToken = default);

    Task<RdpOptimizerOptimizeViewModel> GetOptimizeAsync(CancellationToken cancellationToken = default);

    Task<RdpOptimizationPlan> BuildPlanAsync(
        RdpOptimizationRequestEditor editor,
        CancellationToken cancellationToken = default);

    Task<RdpOptimizationResult> ExecuteAsync(
        RdpOptimizationRequestEditor editor,
        CancellationToken cancellationToken = default);

    Task<RdpOptimizerHistoryViewModel> GetHistoryAsync(CancellationToken cancellationToken = default);

    Task<RdpOptimizationResult> RestoreAsync(
        Guid snapshotId,
        bool reinstallRemovedPackages,
        bool dryRun,
        CancellationToken cancellationToken = default);
}
