// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record NetworkShareMachine(
    Guid Id,
    string DisplayName,
    string Target,
    string? IpAddress,
    string? Workgroup,
    string DiscoveryMethod,
    string? OperatingSystem,
    string? ServerVersion);
