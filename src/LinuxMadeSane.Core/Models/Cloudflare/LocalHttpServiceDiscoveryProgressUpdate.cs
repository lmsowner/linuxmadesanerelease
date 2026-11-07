// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record LocalHttpServiceDiscoveryProgressUpdate(
    string Message,
    int ProbedCount,
    int TotalProbeCount,
    int FoundCount,
    LocalHttpServiceEndpoint? FoundEndpoint = null,
    bool IsCompleted = false);
