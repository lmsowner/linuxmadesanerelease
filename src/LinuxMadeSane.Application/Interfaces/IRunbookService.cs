using LinuxMadeSane.Application.Contracts;

namespace LinuxMadeSane.Application.Interfaces;

// Guardrail: runbook library/editing/execution belongs here rather than on
// IManagedHostService so host lifecycle and runbook behavior can evolve independently.
public interface IRunbookService
{
    Task<IReadOnlyList<CommandLibraryItem>> ListCommandsAsync(CancellationToken cancellationToken = default);

    Task<RunbookExecutionResultViewModel> RunRunbookAsync(
        Guid runbookId,
        IProgress<RunbookExecutionProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default);

    Task<Guid> SaveRunbookAsync(RunbookEditor editor, CancellationToken cancellationToken = default);

    Task DeleteRunbookAsync(Guid runbookId, CancellationToken cancellationToken = default);

    Task SetRunbookHostsAsync(Guid runbookId, IReadOnlyList<Guid> hostIds, CancellationToken cancellationToken = default);

    Task SetHostRunbookAssignmentsAsync(Guid hostId, IReadOnlyList<Guid> runbookIds, CancellationToken cancellationToken = default);

    Task SetCommandQuickAccessAsync(Guid commandId, bool isQuickAccess, CancellationToken cancellationToken = default);

    Task<StarterRunbookImportResult> ImportStarterRunbooksAsync(CancellationToken cancellationToken = default);
}
