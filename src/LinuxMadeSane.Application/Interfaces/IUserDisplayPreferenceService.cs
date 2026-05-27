// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Interfaces;

public interface IUserDisplayPreferenceService
{
    Task<UserDisplayPreference?> GetAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<UserDisplayPreference> SaveAsync(
        Guid userId,
        string themePaletteId,
        string themeMode,
        int fontScalePercent,
        CancellationToken cancellationToken = default);

    Task<UserDisplayPreference> SaveTerminalCopyOnSelectAsync(
        Guid userId,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<UserDisplayPreference> SaveDockerAiActionsApprovedAsync(
        Guid userId,
        bool approved,
        CancellationToken cancellationToken = default);
}
