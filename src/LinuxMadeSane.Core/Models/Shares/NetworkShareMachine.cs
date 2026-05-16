// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

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
