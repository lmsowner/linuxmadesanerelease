// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.LocalAi;
using LinuxMadeSane.Infrastructure.Services;

namespace LinuxMadeSane.Web;

public static class LocalAiPeerApiEndpointExtensions
{
    public static IEndpointRouteBuilder MapLocalAiPeerApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/local-ai/v1/models", async (
            HttpContext context,
            LocalAiPeerSharingService peerSharing) =>
        {
            var result = await peerSharing.ForwardAsync(
                ReadBearerToken(context.Request),
                "models",
                null,
                context.RequestAborted);
            await WriteResponseAsync(context, result);
        });

        endpoints.MapPost("/api/local-ai/v1/chat/completions", async (
            HttpContext context,
            LocalAiPeerSharingService peerSharing) =>
        {
            var body = await ReadRequestBodyWithLimitAsync(
                context.Request,
                LocalAiPeerSharingService.MaximumRequestBytes,
                context.RequestAborted);
            if (body is null)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await context.Response.WriteAsJsonAsync(
                    new { error = "The AI request is too large." },
                    context.RequestAborted);
                return;
            }

            var result = await peerSharing.ForwardAsync(
                ReadBearerToken(context.Request),
                "chat/completions",
                body,
                context.RequestAborted);
            await WriteResponseAsync(context, result);
        });

        return endpoints;
    }

    private static string? ReadBearerToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[prefix.Length..].Trim()
            : null;
    }

    private static async Task<byte[]?> ReadRequestBodyWithLimitAsync(
        HttpRequest request,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > maximumBytes)
        {
            return null;
        }

        await using var buffer = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maximumBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }

    private static async Task WriteResponseAsync(
        HttpContext context,
        LocalAiPeerProxyResult? result)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (result is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsJsonAsync(
                new { error = "A valid Local AI sharing access key is required." },
                context.RequestAborted);
            return;
        }

        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = result.ContentType;
        await context.Response.Body.WriteAsync(result.Body, context.RequestAborted);
    }
}
