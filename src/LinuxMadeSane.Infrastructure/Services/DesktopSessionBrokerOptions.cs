// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class DesktopSessionBrokerOptions
{
    public bool Enabled { get; set; } = true;

    public string SocketPath { get; set; } = "/run/linuxmadesane/desktop-session.sock";

    public int Backlog { get; set; } = 16;

    public int StaleAfterSeconds { get; set; } = 120;
}
