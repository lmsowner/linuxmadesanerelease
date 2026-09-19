// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.LocalAi;
using LinuxMadeSane.Infrastructure.Services;

namespace LinuxMadeSane.Web;

public static class LocalAiPeerApiEndpointExtensions
{
    public static async Task AddLocalAiServiceAddressesAsync(this WebApplication app)
    {
        var explicitUrls =
            app.Configuration["URLS"] ??
            app.Configuration["ASPNETCORE_URLS"] ??
            app.Configuration["DOTNET_URLS"];
        var configuredUrls = !string.IsNullOrWhiteSpace(explicitUrls)
            ? explicitUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : app.Configuration.GetSection("Server:Urls").Get<string[]>() ?? [];
        foreach (var configuredUrl in configuredUrls)
        {
            if (!app.Urls.Contains(configuredUrl, StringComparer.OrdinalIgnoreCase))
            {
                app.Urls.Add(configuredUrl);
            }
        }

        using var scope = app.Services.CreateScope();
        var settings = await scope.ServiceProvider
            .GetRequiredService<ILocalAiEngineStore>()
            .GetSettingsAsync();
        if (!Uri.TryCreate(settings.RuntimeEndpoint, UriKind.Absolute, out var runtimeEndpoint) ||
            runtimeEndpoint.Scheme != Uri.UriSchemeHttp)
        {
            return;
        }

        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
            .Distinct()
            .OrderBy(address => address.ToString(), StringComparer.Ordinal)
            .ToArray();

        foreach (var address in addresses)
        {
            var serviceAddress = $"http://{address}:{runtimeEndpoint.Port}";
            if (!app.Urls.Contains(serviceAddress, StringComparer.OrdinalIgnoreCase))
            {
                app.Urls.Add(serviceAddress);
            }
        }
    }

    public static IEndpointRouteBuilder MapLocalAiPeerApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/models", async (
            HttpContext context,
            LocalAiPeerSharingService peerSharing) =>
        {
            if (!await peerSharing.IsSharedEnginePortAsync(context.Connection.LocalPort, context.RequestAborted))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var result = await peerSharing.ForwardAsync(
                ReadBearerToken(context.Request),
                "models",
                null,
                context.RequestAborted);
            await WriteResponseAsync(context, result);
        });

        endpoints.MapPost("/v1/chat/completions", async (
            HttpContext context,
            LocalAiPeerSharingService peerSharing) =>
        {
            if (!await peerSharing.IsSharedEnginePortAsync(context.Connection.LocalPort, context.RequestAborted))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

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
