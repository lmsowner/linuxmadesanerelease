// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.DesktopSession;

namespace LinuxMadeSane.DesktopHelper;

public sealed class DesktopAssistantNativeMessageBus
{
    public event Action<DesktopAssistantNativeTheme>? ThemeChanged;

    public void PublishThemeChanged(DesktopAssistantNativeTheme theme) =>
        ThemeChanged?.Invoke(theme);
}
