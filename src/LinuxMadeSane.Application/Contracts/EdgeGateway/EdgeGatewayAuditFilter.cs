// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed class EdgeGatewayAuditFilter
{
    public string Hostname { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public DateTimeOffset? FromUtc { get; set; }
    public DateTimeOffset? ToUtc { get; set; }
    public int Take { get; set; } = 250;
}
