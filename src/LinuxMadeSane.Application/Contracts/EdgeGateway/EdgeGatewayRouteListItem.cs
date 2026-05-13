using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayRouteListItem(
    Guid Id,
    bool Enabled,
    string DisplayName,
    string Hostname,
    string DomainName,
    string TargetUrl,
    EdgeGatewayAuthMode AuthMode,
    EdgeGatewayDiagnosticStatus LastTestStatus,
    string LastTestMessage,
    DateTimeOffset UpdatedAt);
