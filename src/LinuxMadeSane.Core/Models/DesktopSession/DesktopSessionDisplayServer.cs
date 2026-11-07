// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.DesktopSession;

public enum DesktopSessionDisplayServer
{
    Headless = 0,
    X11 = 1,
    Wayland = 2,
    X11AndWayland = 3
}
