using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayDiagnosticResult(
    EdgeGatewayDiagnosticStatus Status,
    string Summary,
    IReadOnlyList<string> Checks);
