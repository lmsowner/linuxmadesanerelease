// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
