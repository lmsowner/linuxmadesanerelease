// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;

namespace LinuxMadeSane.Web;

public static class WebsiteEmailRelayEndpointExtensions
{
    public static void MapWebsiteEmailRelay(this WebApplication app)
    {
        app.MapPost("/internal/website-email/send", HandleAsync).DisableAntiforgery();
        app.MapPost("/internal/portal-email/send", HandlePortalAsync).DisableAntiforgery();
    }

    public static Task<IResult> HandleAsync(HttpContext context, IConfiguration configuration,
        IEmailDeliveryService delivery) => HandleCoreAsync(context, configuration, delivery, false);

    public static Task<IResult> HandlePortalAsync(HttpContext context, IConfiguration configuration, IEmailDeliveryService delivery) => HandleCoreAsync(context, configuration, delivery, true);

    private static async Task<IResult> HandleCoreAsync(HttpContext context, IConfiguration configuration,
        IEmailDeliveryService delivery, bool portal)
    {
        context.Response.Headers.CacheControl = "no-store";
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
            return Results.NotFound();

        var keyPath = configuration[portal ? "PortalEmailRelay:KeyFilePath" : "WebsiteEmailRelay:KeyFilePath"];
        var recipient = configuration["WebsiteEmailRelay:RecipientAddress"];
        if (string.IsNullOrWhiteSpace(keyPath) || (!portal && !MailAddress.TryCreate(recipient, out _)))
            return Results.StatusCode(503);
        string key;
        try { key = (await File.ReadAllTextAsync(keyPath, context.RequestAborted)).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.StatusCode(503); }
        var authorization = context.Request.Headers.Authorization.ToString();
        if (key.Length < 32 || authorization.Length > 1024 || !authorization.StartsWith("Bearer ", StringComparison.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)),
                SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..]))))
            return Results.Unauthorized();
        if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
        if (context.Request.ContentLength is > 65536) return Results.StatusCode(413);
        context.Request.Body = new Microsoft.AspNetCore.WebUtilities.FileBufferingReadStream(context.Request.Body, 65536, 65536, Path.GetTempPath());
        context.Response.RegisterForDispose(context.Request.Body);
        WebsiteEmailRequest? request;
        try { request = await context.Request.ReadFromJsonAsync<WebsiteEmailRequest>(context.RequestAborted); }
        catch (JsonException) { return Results.BadRequest(); }
        catch (IOException) { return Results.StatusCode(413); }
        if (request is null || request.Purpose != (portal ? "portal-authentication" : "website-contact") ||
            !MailAddress.TryCreate(request.To, out var target) ||
            (!portal && !string.Equals(request.To, recipient, StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length > 300 || request.Subject.IndexOfAny(['\r', '\n']) >= 0 ||
            string.IsNullOrWhiteSpace(request.HtmlBody) || request.HtmlBody.Length > 32768)
            return Results.BadRequest(new { succeeded = false, message = "Invalid website contact message." });
        var result = await delivery.SendHtmlAsync(target.Address, request.Subject, request.HtmlBody, context.RequestAborted);
        return Results.Json(new { succeeded = result.Succeeded,
            message = result.Succeeded ? "Email accepted." : "Email delivery is temporarily unavailable." },
            statusCode: result.Succeeded ? 200 : 502);
    }

    public sealed record WebsiteEmailRequest(string Purpose, string To, string Subject, string HtmlBody);
}
