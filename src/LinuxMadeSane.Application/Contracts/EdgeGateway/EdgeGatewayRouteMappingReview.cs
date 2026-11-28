// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayRouteMappingReview(
    string Hostname, string RelayHostname, string ReviewToken, IReadOnlyList<string> ExistingMappings)
{
    public bool HasConflict => ExistingMappings.Count > 0;
}
