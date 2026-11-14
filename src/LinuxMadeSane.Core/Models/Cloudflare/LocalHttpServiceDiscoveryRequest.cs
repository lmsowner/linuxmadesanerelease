// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record LocalHttpServiceDiscoveryRequest(
    bool IncludeLocalhost = true,
    bool IncludeLan = true,
    bool IncludeTailnet = false,
    bool IncludeDocker = false,
    IReadOnlyList<LocalHttpServiceEndpoint>? PreferredEndpoints = null,
    bool ForceRescan = false);
