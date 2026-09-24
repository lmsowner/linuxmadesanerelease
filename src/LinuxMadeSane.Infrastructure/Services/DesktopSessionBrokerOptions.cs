// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class DesktopSessionBrokerOptions
{
    public bool Enabled { get; set; } = true;

    public string SocketPath { get; set; } = "/run/linuxmadesane/desktop-session.sock";

    public int Backlog { get; set; } = 16;

    public int StaleAfterSeconds { get; set; } = 120;
}
