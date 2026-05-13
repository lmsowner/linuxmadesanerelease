using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayRuntimeComponentStatus(
    EdgeGatewayDiagnosticStatus Status,
    string Label,
    string Message);
