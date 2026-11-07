// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class UserDisplayPreferenceEntity
{
    public Guid UserId { get; set; }
    public string ThemePaletteId { get; set; } = string.Empty;
    public string ThemeMode { get; set; } = string.Empty;
    public int FontScalePercent { get; set; }
    public bool TerminalCopyOnSelect { get; set; }
    public bool DockerAiActionsApproved { get; set; }
    public string NavigationOrderJson { get; set; } = "[]";
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
