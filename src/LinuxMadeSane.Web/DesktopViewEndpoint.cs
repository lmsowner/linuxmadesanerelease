// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
namespace LinuxMadeSane.Web;
public static class DesktopViewEndpoint
{
    public static void MapDesktopView(this WebApplication app)
    {
        app.MapPost("/remote-desktop/host/{hostId:guid}/open", async (HttpContext context, Guid hostId,
            IAntiforgery antiforgery, IManagedHostStore hosts, DesktopViewIntents intents) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            try
            {
                var owner = RemoteDesktopEndpointExtensions.RequestUserId(context);
                await antiforgery.ValidateRequestAsync(context);
                var host = await hosts.GetAsync(hostId, context.RequestAborted) ?? (AiLocalMachine.IsLocalMachine(hostId) ? AiLocalMachine.CreateManagedHost() : null);
                if (host is null || !ManagedHostCapabilities.IsLmsHost(host)) return Results.NotFound();
                return Results.Redirect($"/desktop/view/{hostId}?intent={intents.Issue(owner, hostId)}");
            }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
            catch (AntiforgeryValidationException) { return Results.BadRequest("Refresh LMS and click View desktop again."); }
        });
    }
}
