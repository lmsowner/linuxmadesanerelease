// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class EdgeGatewayTemporaryIpApprovalGrantEntity
{
    public Guid Id { get; set; }
    public Guid RouteId { get; set; }
    public string RouteName { get; set; } = string.Empty;
    public string PublicHostname { get; set; } = string.Empty;
    public string TargetPathPrefix { get; set; } = string.Empty;
    public string SourceIp { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public DateTimeOffset ApprovedUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public DateTimeOffset IdleExpiresAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
