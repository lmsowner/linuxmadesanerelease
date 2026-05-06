namespace LinuxMadeSane.Core.Models;

public sealed record TrustedNetworkAccessResult(
    string RemoteAddress,
    string RequestHost,
    bool IsTrusted,
    string? MatchedRuleLabel,
    bool IsLocalRequestTarget,
    bool RequiresAuthentication,
    bool IsAllowed,
    bool IsTrustedAccessEnabled,
    bool IsAuthenticationEnabled);
