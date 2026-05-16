// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Interfaces;

public interface IAiProviderSettingsService
{
    Task<AiProviderSettingsPageViewModel> GetPageAsync(CancellationToken cancellationToken = default);
    Task<AiProviderSettingsEditorContextViewModel> GetEditorAsync(string? providerKey = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiProviderModelOption>> RefreshModelCatalogAsync(AiProviderSettingsEditor editor, CancellationToken cancellationToken = default);
    Task<string> SaveAsync(AiProviderSettingsEditor editor, CancellationToken cancellationToken = default);
    Task<AiProviderConnectionTestResult> TestAsync(AiProviderSettingsEditor editor, CancellationToken cancellationToken = default);
}
