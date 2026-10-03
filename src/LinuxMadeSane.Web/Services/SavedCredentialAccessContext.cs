// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using System.Security.Claims;
using System.Net;
using LinuxMadeSane.Core.Abstractions;
using Microsoft.AspNetCore.Components.Authorization;

namespace LinuxMadeSane.Web.Services;

public sealed class SavedCredentialAccessContext(AuthenticationStateProvider authentication,
    ISecurityUserStore users, IHttpContextAccessor http) : ISavedCredentialAccessContext
{
    // A scoped instance belongs to one server circuit. Capture its transport at creation;
    // HttpContext is not available during every subsequent interactive callback.
    private readonly bool secureTransport = http.HttpContext is { } context &&
        (context.Request.IsHttps || context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address));

    public async Task<Guid?> GetAuthenticatedUserIdAsync(CancellationToken token = default)
    {
        if (!secureTransport) return null;
        var principal = (await authentication.GetAuthenticationStateAsync()).User;
        if (principal.Identity?.IsAuthenticated != true || !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return null;
        var user = await users.GetAsync(id, token);
        return user?.IsEnabled == true ? id : null;
    }
}
