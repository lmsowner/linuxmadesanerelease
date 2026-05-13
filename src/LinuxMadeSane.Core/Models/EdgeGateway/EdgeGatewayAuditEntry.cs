using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.EdgeGateway;

public sealed record EdgeGatewayAuditEntry(
    Guid Id,
    DateTimeOffset TimestampUtc,
    string Hostname,
    Guid? RouteId,
    string RequestedPath,
    string SourceIp,
    string UserEmail,
    EdgeGatewayDecision Decision,
    string Reason,
    EdgeGatewayAuthMode AuthMode);
