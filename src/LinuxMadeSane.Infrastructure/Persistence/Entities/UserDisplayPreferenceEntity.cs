// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class UserDisplayPreferenceEntity
{
    public Guid UserId { get; set; }
    public string ThemePaletteId { get; set; } = string.Empty;
    public string ThemeMode { get; set; } = string.Empty;
    public int FontScalePercent { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
