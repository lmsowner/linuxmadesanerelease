// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using Microsoft.AspNetCore.SignalR;

namespace LinuxMadeSane.Web.Services;

public sealed class ConnectedUserHubFilter(ConnectedUserRegistry registry) : IHubFilter
{
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var httpContext = context.Context.GetHttpContext();
        registry.Connect(context.Context.ConnectionId, context.Context.User ?? new System.Security.Claims.ClaimsPrincipal(),
            httpContext?.Connection.RemoteIpAddress);
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
