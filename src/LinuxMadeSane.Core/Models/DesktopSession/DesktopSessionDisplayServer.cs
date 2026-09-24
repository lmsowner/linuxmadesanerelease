// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.DesktopSession;

public enum DesktopSessionDisplayServer
{
    Headless = 0,
    X11 = 1,
    Wayland = 2,
    X11AndWayland = 3
}
