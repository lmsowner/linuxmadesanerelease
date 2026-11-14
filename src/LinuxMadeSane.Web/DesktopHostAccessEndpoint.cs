// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Net;
using LinuxMadeSane.Web.Services;
namespace LinuxMadeSane.Web;
public static class DesktopHostAccessEndpoint
{
    public static void MapDesktopHostAccess(this WebApplication app)
    {
        app.MapPost("/internal/lms-tunnel/desktop-access", async (HttpContext context, RemoteLmsTunnelAccessService access, DesktopHostAccess desktops) =>
        {
            var ip = context.Connection.RemoteIpAddress;
            if (ip is null || !IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip) || !PortalAddonUpdateEndpoint.IsLoopbackHost(context.Request.Host.Host)) return Results.NotFound();
            context.Response.Headers.CacheControl = "no-store";
            var request = await context.Request.ReadFromJsonAsync<DesktopHostAccessRequest>(cancellationToken: context.RequestAborted);
            if (request is null || !access.AuthorizeUpdate(ip, context.Request.Host.Host, request.Token)) return Results.Unauthorized();
            try { return Results.Json(await desktops.GetAsync(request.Prepare, context.RequestAborted)); }
            catch (InvalidOperationException error) { return Results.Json(new { error = error.Message }, statusCode: 409); }
        }).DisableAntiforgery();
    }
}
