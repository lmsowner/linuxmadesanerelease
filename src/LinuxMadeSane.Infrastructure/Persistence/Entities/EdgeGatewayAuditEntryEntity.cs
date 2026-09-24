// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class EdgeGatewayAuditEntryEntity
{
    public Guid Id { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public Guid? RouteId { get; set; }
    public string RequestedPath { get; set; } = string.Empty;
    public string SourceIp { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public int Decision { get; set; }
    public string Reason { get; set; } = string.Empty;
    public int AuthMode { get; set; }
}
