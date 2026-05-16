// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum AiSafeChangeTargetKind
{
    File = 0,
    Service = 1,
    Package = 2,
    User = 3,
    Group = 4,
    FirewallRule = 5,
    ProxyConfig = 6,
    Directory = 7,
    Command = 8,
    SystemState = 9
}
