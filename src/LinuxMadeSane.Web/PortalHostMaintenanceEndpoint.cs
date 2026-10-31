// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Net;
using LinuxMadeSane.Web.Services;
namespace LinuxMadeSane.Web;
public static class PortalHostMaintenanceEndpoint
{
    public static void MapPortalHostMaintenance(this WebApplication app)
    {
        app.MapPost("/internal/lms-tunnel/portal-maintenance",async(HttpContext c,RemoteLmsTunnelAccessService access,PortalHostMaintenanceService maintenance)=>
        {
            if(c.Connection.RemoteIpAddress is not{}ip||!IPAddress.IsLoopback(ip)||!PortalAddonUpdateEndpoint.IsLoopbackHost(c.Request.Host.Host))return Results.NotFound();
            var request=await c.Request.ReadFromJsonAsync<PortalHostMaintenanceRequest>(cancellationToken:c.RequestAborted);
            if(request is null||!access.AuthorizeUpdate(c.Connection.RemoteIpAddress,c.Request.Host.Host,request.Token))return Results.Unauthorized();
            c.Response.Headers.CacheControl="no-store";
            try{var status=await maintenance.RequestAsync(request,c.RequestAborted);return status is null?Results.Json(new{state="Not started"},statusCode:404):Results.Json(status);}
            catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}
            catch(InvalidOperationException e){return Results.Json(new{error=e.Message},statusCode:409);}
        }).DisableAntiforgery();
    }
}
