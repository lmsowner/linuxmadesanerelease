// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Ai;

namespace LinuxMadeSane.Application.Interfaces;

public interface IAiThreadService
{
    Task<AiOverviewViewModel> GetOverviewAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiChatThreadListItemViewModel>> ListThreadsAsync(CancellationToken cancellationToken = default);
    Task<Guid> CreateThreadAsync(CancellationToken cancellationToken = default);
    Task<AiChatThreadEditorContextViewModel> GetEditorAsync(Guid? threadId = null, CancellationToken cancellationToken = default);
    Task<Guid> SaveThreadAsync(AiChatThreadEditor editor, CancellationToken cancellationToken = default);
    Task UpdateProviderSelectionAsync(Guid threadId, string providerKey, string modelId, CancellationToken cancellationToken = default);
}
