// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class EdgeGatewayTemporaryIpApprovalRequestEntity
{
    public Guid Id { get; set; }
    public Guid RouteId { get; set; }
    public string RouteName { get; set; } = string.Empty;
    public string PublicHostname { get; set; } = string.Empty;
    public string TargetPathPrefix { get; set; } = string.Empty;
    public string SourceIp { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string RequestedUrl { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? LastEmailSentUtc { get; set; }
    public int EmailSendCount { get; set; }
    public string ApprovalTokenHash { get; set; } = string.Empty;
    public DateTimeOffset? ApprovalTokenExpiresAtUtc { get; set; }
    public DateTimeOffset? ApprovedUtc { get; set; }
    public string LastEmailStatus { get; set; } = string.Empty;
}
