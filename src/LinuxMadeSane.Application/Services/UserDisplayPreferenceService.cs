// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using System.Text.Json;

namespace LinuxMadeSane.Application.Services;

public sealed class UserDisplayPreferenceService(IUserDisplayPreferenceStore store) : IUserDisplayPreferenceService
{
    private const string DefaultPaletteId = "smooth-grey";
    private const string DefaultThemeMode = "auto";
    private const int MinimumFontScalePercent = 90;
    private const int MaximumFontScalePercent = 180;

    public Task<UserDisplayPreference?> GetAsync(Guid userId, CancellationToken cancellationToken = default) =>
        store.GetAsync(userId, cancellationToken);

    public async Task<UserDisplayPreference> SaveAsync(
        Guid userId,
        string themePaletteId,
        string themeMode,
        int fontScalePercent,
        CancellationToken cancellationToken = default)
    {
        var existing = await store.GetAsync(userId, cancellationToken);
        var preference = new UserDisplayPreference(
            userId,
            NormalizePaletteId(themePaletteId),
            NormalizeThemeMode(themeMode),
            Math.Clamp(fontScalePercent, MinimumFontScalePercent, MaximumFontScalePercent),
            existing?.TerminalCopyOnSelect ?? false,
            existing?.DockerAiActionsApproved ?? false,
            existing?.NavigationOrderJson ?? "[]",
            DateTimeOffset.UtcNow);

        await store.SaveAsync(preference, cancellationToken);
        return preference;
    }

    public async Task<UserDisplayPreference> SaveTerminalCopyOnSelectAsync(
        Guid userId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var existing = await store.GetAsync(userId, cancellationToken);
        var preference = new UserDisplayPreference(
            userId,
            existing?.ThemePaletteId ?? DefaultPaletteId,
            existing?.ThemeMode ?? DefaultThemeMode,
            existing?.FontScalePercent ?? 100,
            enabled,
            existing?.DockerAiActionsApproved ?? false,
            existing?.NavigationOrderJson ?? "[]",
            DateTimeOffset.UtcNow);

        await store.SaveAsync(preference, cancellationToken);
        return preference;
    }

    public async Task<UserDisplayPreference> SaveDockerAiActionsApprovedAsync(
        Guid userId,
        bool approved,
        CancellationToken cancellationToken = default)
    {
        var existing = await store.GetAsync(userId, cancellationToken);
        var preference = new UserDisplayPreference(
            userId,
            existing?.ThemePaletteId ?? DefaultPaletteId,
            existing?.ThemeMode ?? DefaultThemeMode,
            existing?.FontScalePercent ?? 100,
            existing?.TerminalCopyOnSelect ?? false,
            approved,
            existing?.NavigationOrderJson ?? "[]",
            DateTimeOffset.UtcNow);

        await store.SaveAsync(preference, cancellationToken);
        return preference;
    }

    public async Task<UserDisplayPreference> SaveNavigationOrderAsync(
        Guid userId,
        IReadOnlyList<string> navigationOrder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(navigationOrder);
        if (navigationOrder.Count > 64 || navigationOrder.Any(path =>
                string.IsNullOrWhiteSpace(path) || path.Length > 128 || !path.StartsWith('/')) ||
            navigationOrder.Distinct(StringComparer.Ordinal).Count() != navigationOrder.Count)
        {
            throw new ArgumentException("Navigation order contains invalid or duplicate paths.", nameof(navigationOrder));
        }

        var existing = await store.GetAsync(userId, cancellationToken);
        var preference = new UserDisplayPreference(
            userId,
            existing?.ThemePaletteId ?? DefaultPaletteId,
            existing?.ThemeMode ?? DefaultThemeMode,
            existing?.FontScalePercent ?? 100,
            existing?.TerminalCopyOnSelect ?? false,
            existing?.DockerAiActionsApproved ?? false,
            JsonSerializer.Serialize(navigationOrder),
            DateTimeOffset.UtcNow);

        await store.SaveAsync(preference, cancellationToken);
        return preference;
    }

    private static string NormalizePaletteId(string paletteId) =>
        string.IsNullOrWhiteSpace(paletteId)
            ? DefaultPaletteId
            : paletteId.Trim();

    private static string NormalizeThemeMode(string themeMode)
    {
        var normalized = (themeMode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "light" or "dark" or "auto"
            ? normalized
            : DefaultThemeMode;
    }
}
