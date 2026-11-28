// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Security.Claims;
using System.Net;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Abstractions;
using Microsoft.AspNetCore.Components.Authorization;

namespace LinuxMadeSane.Web.Services;

public sealed class SavedCredentialAccessContext(AuthenticationStateProvider authentication,
    ISecurityUserStore users, IHttpContextAccessor http, ITrustedNetworkAccessService? networks = null, RemoteDesktop.RemoteDesktopTrustedAccess? bridge = null) : ISavedCredentialAccessContext
{
    // Capture the circuit's actual request identity and middleware decision, rather
    // than treating a missing HttpContext during callbacks as authorisation.
    private readonly IPAddress? remoteAddress = http.HttpContext?.Connection.RemoteIpAddress;
    private readonly string? requestHost = http.HttpContext?.Request.Host.Host;
    private readonly bool trustedRequest = http.HttpContext?.Items["LmsTrustedNetworkAccess"] is TrustedNetworkAccessResult { IsTrusted: true };

    public async Task<Guid?> GetAuthenticatedUserIdAsync(CancellationToken token = default)
    {
        if (bridge?.User is { } bridgeUser) return bridgeUser == Guid.Empty || (await users.GetAsync(bridgeUser, token))?.IsEnabled == true ? bridgeUser : null;
        var principal = (await authentication.GetAuthenticationStateAsync()).User;
        if (principal.Identity?.IsAuthenticated == true)
        {
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return null;
            var user = await users.GetAsync(id, token);
            return user?.IsEnabled == true ? id : null;
        }
        if (trustedRequest || networks is not null && remoteAddress is not null &&
            (await networks.EvaluateAsync(remoteAddress, requestHost, token)).IsTrusted)
            return Guid.Empty; // Host administrator admitted through configured trusted access.
        return null;
    }
}
