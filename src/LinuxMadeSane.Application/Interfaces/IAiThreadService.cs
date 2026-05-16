// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

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
