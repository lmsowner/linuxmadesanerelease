// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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

    Task<RunbookExecutionResultViewModel> RunSelectionAsync(Guid runbookId, Guid hostId,
        IReadOnlyList<string> paths, bool isFolder, IProgress<RunbookExecutionProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<RunbookExecutionResultViewModel> RunSelectionAsync(Guid runbookId, Guid hostId,
        IReadOnlyList<string> paths, IReadOnlyList<bool> folderFlags,
        IProgress<RunbookExecutionProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<Guid> SaveRunbookAsync(RunbookEditor editor, CancellationToken cancellationToken = default);

    Task DeleteRunbookAsync(Guid runbookId, CancellationToken cancellationToken = default);

    Task SetRunbookHostsAsync(Guid runbookId, IReadOnlyList<Guid> hostIds, CancellationToken cancellationToken = default);

    Task SetHostRunbookAssignmentsAsync(Guid hostId, IReadOnlyList<Guid> runbookIds, CancellationToken cancellationToken = default);

    Task SetCommandQuickAccessAsync(Guid commandId, bool isQuickAccess, CancellationToken cancellationToken = default);

    Task SetCommandGlobalFavoriteAsync(Guid commandId, bool isGlobalFavorite, CancellationToken cancellationToken = default);

    Task<StarterRunbookImportResult> ImportStarterRunbooksAsync(CancellationToken cancellationToken = default);
}
