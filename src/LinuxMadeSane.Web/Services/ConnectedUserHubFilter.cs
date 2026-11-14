// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using Microsoft.AspNetCore.SignalR;

namespace LinuxMadeSane.Web.Services;

public sealed class ConnectedUserHubFilter(ConnectedUserRegistry registry, RemoteLmsTunnelAccessService tunnelAccess) : IHubFilter
{
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var httpContext = context.Context.GetHttpContext();
        registry.Connect(context.Context.ConnectionId, context.Context.User ?? new System.Security.Claims.ClaimsPrincipal(),
            ConnectedUserClientAddress.GetAddress(httpContext),
            tunnelAccess.GetPortalUserEmail(httpContext?.Connection.RemoteIpAddress,
                httpContext?.Request.Cookies[RemoteLmsTunnelAccessService.CookieName]));
        try
        {
            await next(context);
        }
        catch
        {
            registry.Disconnect(context.Context.ConnectionId);
            throw;
        }
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        registry.Disconnect(context.Context.ConnectionId);
        await next(context, exception);
    }
}
