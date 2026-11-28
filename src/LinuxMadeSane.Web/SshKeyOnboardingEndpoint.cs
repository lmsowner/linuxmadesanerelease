// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Net;
using LinuxMadeSane.Infrastructure.Services;
using LinuxMadeSane.Web.Services;

namespace LinuxMadeSane.Web;

public static class SshKeyOnboardingEndpoint
{
    public static void MapSshKeyOnboarding(this WebApplication app)
    {
        app.MapPost("/internal/lms-tunnel/ssh-key-setup", async (HttpContext context,
            RemoteLmsTunnelAccessService access, SshKeyOnboardingService onboarding) =>
        {
            var ip = context.Connection.RemoteIpAddress;
            if (ip is null || !IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip) ||
                !PortalAddonUpdateEndpoint.IsLoopbackHost(context.Request.Host.Host)) return Results.NotFound();
            context.Response.Headers.CacheControl = "no-store";
            var request = await context.Request.ReadFromJsonAsync<RemoteSshKeySetupRequest>(cancellationToken: context.RequestAborted);
            if (request is null || !access.AuthorizeUpdate(ip, context.Request.Host.Host, request.Token)) return Results.Unauthorized();
            try
            {
                switch (request.Action)
                {
                    case "begin" when request.TrialId is null:
                        var trial = await onboarding.BeginAsync(request.UserName, request.PublicKey, context.RequestAborted);
                        return Results.Json(new RemoteSshKeySetupResponse(trial.Id, "Key and login policy prepared. Awaiting a fresh key login."));
                    case "confirm" when request.TrialId is { } id:
                        await onboarding.ConfirmAsync(id, request.PublicKey, context.RequestAborted);
                        return Results.Json(new RemoteSshKeySetupResponse(id, "Key login verified and policy saved."));
                    case "revert" when request.TrialId is { } id:
                        await onboarding.RevertAsync(id, context.RequestAborted);
                        return Results.Json(new RemoteSshKeySetupResponse(id, "Previous SSH settings restored."));
                    default: return Results.BadRequest();
                }
            }
            catch (InvalidOperationException error) { return Results.Json(new { error = error.Message }, statusCode: 409); }
        }).DisableAntiforgery();
    }
}

public sealed record RemoteSshKeySetupRequest(string Token, string Action, string UserName, string PublicKey, Guid? TrialId = null);
public sealed record RemoteSshKeySetupResponse(Guid TrialId, string Message);
