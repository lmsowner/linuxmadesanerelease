// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using LinuxMadeSane.Application.Services;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class TrustedNetworkAccessService : ITrustedNetworkAccessService
{
    private readonly ITrustedNetworkStore trustedNetworkStore;
    private readonly TrustedNetworkAccessTrialService trustedNetworkAccessTrialService;

    public TrustedNetworkAccessService(
        ITrustedNetworkStore trustedNetworkStore,
        ISecurityUserStore securityUserStore,
        TrustedNetworkAccessTrialService? trustedNetworkAccessTrialService = null)
    {
        this.trustedNetworkStore = trustedNetworkStore;
        this.trustedNetworkAccessTrialService = trustedNetworkAccessTrialService ?? new TrustedNetworkAccessTrialService();
    }

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

        var entries = this.trustedNetworkAccessTrialService.GetEffectiveEntries(
            await trustedNetworkStore.ListAsync(cancellationToken));
        var match = TrustedNetworkMatcher.Match(remoteAddress, entries);
        var isLocalRequestTarget = LocalRequestTargetEvaluator.IsLocal(normalizedRequestHost);
        // Account existence must not silently override the saved interface policy.
        // Fresh installs use the installer code for temporary recovery access.
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
            isAuthenticationEnabled,
            match?.DeniedResponseMode ?? NetworkAccessDeniedResponseMode.AccessDeniedPage);
    }

}
