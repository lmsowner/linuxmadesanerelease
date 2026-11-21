// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using LinuxMadeSane.Web.Services;
namespace LinuxMadeSane.Web;

/// <summary>Uses the same loopback-only, single-use update grant as the existing LMS updater.</summary>
public static class PortalAddonUpdateEndpoint
{
    public static void MapPortalAddonUpdate(this WebApplication app)
    {
        app.MapPost("/internal/lms-tunnel/portal-addon-update", async (HttpContext context,
            RemoteLmsTunnelAccessService access) =>
        {
            if(context.Connection.RemoteIpAddress is not {} address || !System.Net.IPAddress.IsLoopback(address) ||
                !IsLoopbackHost(context.Request.Host.Host)) return Results.NotFound();
            var request=await context.Request.ReadFromJsonAsync<RemoteLmsUpdateRequest>(cancellationToken:context.RequestAborted);
            if(request is null || !access.AuthorizeUpdate(context.Connection.RemoteIpAddress,context.Request.Host.Host,request.Token)) return Results.Unauthorized();
            context.Response.Headers.CacheControl="no-store";
            try
            {
                var registration=context.RequestServices.GetRequiredService<PortalAddonBootstrapService>();
                var updates=context.RequestServices.GetRequiredService<ApplicationUpdateService>();
                await registration.UpdateAsync(updates.GetStatus().Channel,token:context.RequestAborted,beforeRestart:async()=>
                {
                    await context.Response.WriteAsJsonAsync(new{message="Signed Portal add-on installed. LMS is restarting."});
                    await context.Response.CompleteAsync();
                });
                return Results.Empty;
            }
            catch(Exception error) when(!context.Response.HasStarted && error is InvalidOperationException or ArgumentException or HttpRequestException or IOException or System.Security.Cryptography.CryptographicException)
            { return Results.Json(new{error="The signed Portal add-on could not be installed. Check that the selected LMS release channel supports the current add-on."},statusCode:409); }
        }).DisableAntiforgery();
    }
    public static bool IsLoopbackHost(string host)=>host.Equals("localhost",StringComparison.OrdinalIgnoreCase)||
        System.Net.IPAddress.TryParse(host.Trim('[',']'),out var ip)&&System.Net.IPAddress.IsLoopback(ip);
}
