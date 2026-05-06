namespace LinuxMadeSane.Core.Models;

public sealed record UserDisplayPreference(
    Guid UserId,
    string ThemePaletteId,
    string ThemeMode,
    int FontScalePercent,
    DateTimeOffset UpdatedAtUtc);
