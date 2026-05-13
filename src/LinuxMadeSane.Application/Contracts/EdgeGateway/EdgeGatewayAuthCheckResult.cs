using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayAuthCheckResult(
    int StatusCode,
    EdgeGatewayDecision Decision,
    string Reason,
    string? RedirectLocation = null,
    string? UserName = null,
    string? UserEmail = null,
    string? Groups = null);
