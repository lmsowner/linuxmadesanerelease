// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Net;
using System.Security.Cryptography;
using System.Text;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Web;

internal static class SftpApiAccess
{
    internal static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool TryNetwork(string text, out System.Net.IPNetwork network)
    {
        if (IPAddress.TryParse(text, out var address))
        {
            address = Normalize(address);
            network = new(address, address.GetAddressBytes().Length * 8);
            return true;
        }
        return System.Net.IPNetwork.TryParse(text, out network);
    }

    internal static bool TryNetworks(string text, out System.Net.IPNetwork[] networks)
    {
        var parsed = new List<System.Net.IPNetwork>();
        foreach (var item in text.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!TryNetwork(item, out var network))
            {
                networks = [];
                return false;
            }
            parsed.Add(network);
        }
        networks = parsed.Distinct().ToArray();
        return true;
    }

    internal static string Issue(SftpApiClient client)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        client.TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(secret)));
        return $"{client.Id:N}.{secret}";
    }

    internal static async Task AuthenticateAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments(SftpServerApi.Prefix))
        {
            await next(context);
            return;
        }
        context.Response.Headers.CacheControl = "no-store";
        if (context.Features.Get<ITlsConnectionFeature>() == null)
        {
            context.Response.StatusCode = 403;
            return;
        }
        var header = context.Request.Headers.Authorization.ToString();
        if (header.Length != 104 || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            header[39] != '.' || !Guid.TryParseExact(header.AsSpan(7,32), "N", out var id))
        {
            Unauthorized(context);
            return;
        }
        var db = context.RequestServices.GetRequiredService<LinuxMadeSaneDbContext>();
        var client = await db.SftpApiClients.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, context.RequestAborted);
        if (client == null || client.TokenHash.Length != 64 || !CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.ASCII.GetBytes(header[40..])), Convert.FromHexString(client.TokenHash)))
        {
            Unauthorized(context);
            return;
        }
        var peer = context.Items[Program.OriginalConnectionRemoteIpAddressItemKey] as IPAddress;
        var local = context.Connection.LocalIpAddress;
        if (local == null || peer == null || !IPAddress.TryParse(client.LocalAddress, out var expected) ||
            !Normalize(local).Equals(expected) || !TryNetworks(client.SourceNetwork, out var networks) ||
            !networks.Any(network => network.Contains(Normalize(peer))))
        {
            context.Response.StatusCode = 403;
            return;
        }
        var size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (size is { IsReadOnly: false })
            size.MaxRequestBodySize = context.Request.HasFormContentType ? SftpApiFiles.RequestLimit : 65536;
        context.Items[typeof(SftpApiClient)] = client.Id;
        await next(context);
    }

    private static void Unauthorized(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        context.Response.StatusCode = 401;
    }
}
