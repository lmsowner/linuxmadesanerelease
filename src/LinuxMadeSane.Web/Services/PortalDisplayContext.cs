// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using LinuxMadeSane.Core.Abstractions.Portal;
namespace LinuxMadeSane.Web.Services;

// Appearance metadata only; never accepts identity or preferences from request headers.
public sealed class PortalDisplayContext(RemoteLmsTunnelAccessService sessions, IPortalConnectionStore connections)
{
    public async Task<string?> GetOriginAsync(HttpContext? context)
    {
        if (context is null || sessions.GetPortalUserEmail(context.Connection.RemoteIpAddress,
                context.Request.Cookies[RemoteLmsTunnelAccessService.CookieName]) is null) return null;
        var settings = await connections.GetAsync(context.RequestAborted);
        return settings?.IsEnabled == true && Uri.TryCreate(settings.PortalBaseUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? uri.GetLeftPart(UriPartial.Authority) : null;
    }
}
