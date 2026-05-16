// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record LocalHttpServiceDiscoveryProgressUpdate(
    string Message,
    int ProbedCount,
    int TotalProbeCount,
    int FoundCount,
    LocalHttpServiceEndpoint? FoundEndpoint = null,
    bool IsCompleted = false);
