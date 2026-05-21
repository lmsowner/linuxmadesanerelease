// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.DesktopHelper;

public sealed class DesktopSessionHelperOptions
{
    public string SocketPath { get; set; } = "/run/linuxmadesane/desktop-session.sock";

    public string LocalLmsUrl { get; set; } = "http://127.0.0.1:5080/desktop-assistant";

    public bool TrayEnabled { get; set; } = true;

    public string TrayIconPath { get; set; } = string.Empty;

    public string TrayIconName { get; set; } = "applications-system";

    public int HeartbeatSeconds { get; set; } = 20;

    public int RetrySeconds { get; set; } = 10;
}
