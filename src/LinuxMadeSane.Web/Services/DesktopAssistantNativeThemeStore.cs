// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.DesktopSession;
using LinuxMadeSane.Core.Abstractions;

namespace LinuxMadeSane.Web.Services;

public sealed class DesktopAssistantNativeThemeStore(
    IDesktopSessionBroker desktopSessionBroker,
    ILogger<DesktopAssistantNativeThemeStore> logger)
{
    private readonly object syncRoot = new();
    private DesktopAssistantNativeTheme current = BuildDefaultTheme();

    public DesktopAssistantNativeTheme Current
    {
        get
        {
            lock (syncRoot)
            {
                return current;
            }
        }
    }

    public void Update(string? paletteId, string? paletteName, string? mode, string? scheme, int fontScalePercent)
    {
        DesktopAssistantNativeTheme updated;
        lock (syncRoot)
        {
            updated = new DesktopAssistantNativeTheme(
                NormalizePaletteId(paletteId),
                string.IsNullOrWhiteSpace(paletteName) ? BuildPaletteName(paletteId) : paletteName.Trim(),
                NormalizeMode(mode),
                NormalizeScheme(scheme, mode),
                Math.Clamp(fontScalePercent <= 0 ? 100 : fontScalePercent, 90, 180));
            if (updated == current)
            {
                return;
            }

            current = updated;
        }

        _ = PublishThemeChangedAsync(updated);
    }

    private async Task PublishThemeChangedAsync(DesktopAssistantNativeTheme theme)
    {
        try
        {
            await desktopSessionBroker.PublishThemeChangedAsync(theme);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not publish the Desktop Assistant theme update.");
        }
    }

    private static string NormalizePaletteId(string? paletteId) =>
        string.IsNullOrWhiteSpace(paletteId)
            ? "smooth-grey"
            : paletteId.Trim();

    private static string NormalizeMode(string? mode)
    {
        var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "light" or "dark" or "auto"
            ? normalized
            : "auto";
    }

    private static string NormalizeScheme(string? scheme, string? mode)
    {
        var normalized = (scheme ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is "light" or "dark")
        {
            return normalized;
        }

        normalized = NormalizeMode(mode);
        if (normalized is "light" or "dark")
        {
            return normalized;
        }

        var hour = DateTimeOffset.Now.Hour;
        return hour >= 7 && hour < 19 ? "light" : "dark";
    }

    private static string BuildPaletteName(string? paletteId) =>
        string.Join(
            ' ',
            NormalizePaletteId(paletteId)
                .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static DesktopAssistantNativeTheme BuildDefaultTheme() =>
        new("smooth-grey", "Smooth Grey", "auto", NormalizeScheme(null, "auto"), 100);
}
