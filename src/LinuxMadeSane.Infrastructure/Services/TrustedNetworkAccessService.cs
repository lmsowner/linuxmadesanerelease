using System.Net;
using LinuxMadeSane.Application.Services;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class TrustedNetworkAccessService(ITrustedNetworkStore trustedNetworkStore) : ITrustedNetworkAccessService
{
    public async Task<TrustedNetworkAccessResult> EvaluateAsync(
        IPAddress? remoteAddress,
        string? requestHost,
        CancellationToken cancellationToken = default)
    {
        var normalizedRemote = remoteAddress is null
            ? "unknown"
            : (remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress).ToString();
        var normalizedRequestHost = string.IsNullOrWhiteSpace(requestHost)
            ? "unknown"
            : requestHost.Trim();

        var entries = await trustedNetworkStore.ListAsync(cancellationToken);
        var match = TrustedNetworkMatcher.Match(remoteAddress, entries);
        var isLocalRequestTarget = LocalRequestTargetEvaluator.IsLocal(normalizedRequestHost);
        var isAuthenticationEnabled = match?.IsEnabled == true && match.IsAuthenticationEnabled;
        var isTrusted = match?.IsEnabled == true && !isAuthenticationEnabled;
        var requiresAuthentication = isAuthenticationEnabled;
        var isAllowed = match?.IsEnabled == true;
        var isTrustedAccessEnabled = isTrusted;

        return new TrustedNetworkAccessResult(
            normalizedRemote,
            normalizedRequestHost,
            isTrusted,
            match?.Label,
            isLocalRequestTarget,
            requiresAuthentication,
            isAllowed,
            isTrustedAccessEnabled,
            isAuthenticationEnabled);
    }
}
