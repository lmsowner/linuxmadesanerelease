// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record DiscoveredSshHost(
    string DisplayName,
    string Target,
    string? IpAddress,
    int Port,
    string ScopeLabel,
    string SourceLabel,
    string? Platform,
    string? SshBanner,
    bool IsLinuxMadeSaneHost = false,
    string? LinuxMadeSaneBaseUrl = null,
    string? LinuxMadeSaneVersion = null);
