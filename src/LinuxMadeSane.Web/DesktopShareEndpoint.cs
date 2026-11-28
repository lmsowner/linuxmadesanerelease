// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Net;
using LinuxMadeSane.Web.Services;
namespace LinuxMadeSane.Web;
public static class DesktopShareEndpoint
{
    public static void MapDesktopShare(this WebApplication app)
    {
        app.MapPost("/internal/lms-tunnel/desktop-share", async (HttpContext context, RemoteLmsTunnelAccessService access, IDesktopShareHost host) =>
        {
            var ip = context.Connection.RemoteIpAddress;
            if (ip is null || !IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip) || !PortalAddonUpdateEndpoint.IsLoopbackHost(context.Request.Host.Host)) return Results.NotFound();
            context.Response.Headers.CacheControl = "no-store";
            var request = await context.Request.ReadFromJsonAsync<DesktopShareRequest>(cancellationToken: context.RequestAborted);
            if (request is null || !access.AuthorizeUpdate(ip, context.Request.Host.Host, request.Token)) return Results.Unauthorized();
            try
            {
                if (request.Action == "inspect") return Results.Json(await host.InspectAsync(context.RequestAborted));
                if (request.Action == "start" && !string.IsNullOrWhiteSpace(request.SessionId)) return Results.Json(await host.StartAsync(request.SessionId, context.RequestAborted));
                if (request.LeaseId is null || request.LeaseToken is null) return Results.BadRequest();
                var lease = new DesktopShareLease(request.LeaseId, request.LeaseToken, "", "", "", 0, "");
                if (request.Action == "renew") return Results.Json(new { active = await host.RenewAsync(lease, context.RequestAborted) });
                if (request.Action == "stop") { await host.StopAsync(lease, context.RequestAborted); return Results.Json(new { active = false }); }
                return Results.BadRequest();
            }
            catch (InvalidOperationException error) { return Results.Json(new { error = error.Message }, statusCode: 409); }
        }).DisableAntiforgery();
    }
}
