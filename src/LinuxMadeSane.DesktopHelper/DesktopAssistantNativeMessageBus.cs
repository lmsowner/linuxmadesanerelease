// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.DesktopSession;

namespace LinuxMadeSane.DesktopHelper;

public sealed class DesktopAssistantNativeMessageBus
{
    public event Action<DesktopAssistantNativeTheme>? ThemeChanged;

    public void PublishThemeChanged(DesktopAssistantNativeTheme theme) =>
        ThemeChanged?.Invoke(theme);
}
