// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Net;
using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Cloudflare;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Web.Services;
using Microsoft.EntityFrameworkCore;
namespace LinuxMadeSane.Web;

public static class PortalDirectDnsEndpoints
{
    public sealed record Request(string Token,string Hostname,string Type,string? Value,bool Delete=false,bool CheckOnly=false);
    public static void MapPortalDirectDns(this WebApplication app)
    {
        app.MapPost("/internal/lms-tunnel/portal-direct-dns",async(HttpContext c,RemoteLmsTunnelAccessService access,LinuxMadeSaneDbContext db,ISecretStore secrets,ICloudflareDnsService dns,ICloudflareZoneService zones,IConfiguration config)=>
        {
            if(c.Connection.RemoteIpAddress is not {} remote||!IPAddress.IsLoopback(remote)||!(c.Request.Host.Host is "localhost" or "127.0.0.1" or "[::1]"))return Results.NotFound();
            if(c.Request.ContentLength>8192)return Results.BadRequest();
            if(c.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is {IsReadOnly:false} size)size.MaxRequestBodySize=8192;
            var request=await c.Request.ReadFromJsonAsync<Request>(c.RequestAborted);
            if(request is null||!access.AuthorizeUpdate(remote,c.Request.Host.Host,request.Token))return Results.Unauthorized();
            var domain=config["PortalDirect:BaseDomain"]??"linuxmadesane.com";
            if(!Regex.IsMatch(request.Hostname,"^i[0-9a-f]{32}[.]direct[.]"+Regex.Escape(domain)+"$",RegexOptions.CultureInvariant)||request.Type is not ("A" or "TXT"))return Results.BadRequest();
            if(!request.Delete&&(request.Type=="A"? (!IPAddress.TryParse(request.Value,out var ip)||!LinuxMadeSane.Connect.Protocol.Models.PortalDirectNetworkPolicy.IsPublic(ip)):!Regex.IsMatch(request.Value??"","^[A-Za-z0-9_-]{43}$")))return Results.BadRequest();
            var savedSettings=await db.CloudflareSettings.AsNoTracking().ToArrayAsync(c.RequestAborted);
            var candidates=savedSettings.OrderByDescending(x=>x.ZoneName==domain).ThenByDescending(x=>x.UpdatedAtUtc);
            string? key=null;string? zoneId=null;
            foreach(var settings in candidates)
            {
                if(settings.ApiTokenSecretReference is null)continue;
                var candidate=await secrets.ResolveSecretAsync(settings.ApiTokenSecretReference,c.RequestAborted);if(candidate is null)continue;
                if(settings.ZoneName==domain){key=candidate;zoneId=settings.ZoneId;break;}
                // Existing credentials can cover multiple zones. Resolve only the exact Portal parent zone.
                try{var zone=(await zones.ListZonesAsync(candidate,c.RequestAborted)).SingleOrDefault(x=>x.Name==domain&&!x.Paused);if(zone is not null){key=candidate;zoneId=zone.Id;break;}}
                catch(CloudflareApiException){ }
            }
            if(key is null||zoneId is null)return Results.Problem("Configure a Cloudflare credential with DNS access to the Portal parent domain on this LMS host first.",statusCode:409);
            if(request.CheckOnly)return Results.Ok(new{ready=true});
            var name=(request.Type=="TXT"?"_acme-challenge.":"")+request.Hostname;var comment="LMS Portal direct "+request.Hostname;
            var records=(await dns.ListRecordsAsync(key,zoneId,c.RequestAborted)).Where(x=>x.Name==name&&x.Type==request.Type).ToArray();
            if(records.Any(x=>x.Comment!=comment))return Results.Conflict("An independently managed DNS record already exists; it was preserved.");
            if(request.Delete){foreach(var record in records)await dns.DeleteRecordAsync(key,zoneId,record.Id,c.RequestAborted);return Results.Ok(new{removed=true});}
            if(records.Length>1)return Results.Conflict("Multiple direct DNS records need review.");
            var desired=new CloudflareDnsRecord(records.FirstOrDefault()?.Id??"",zoneId,name,request.Type,request.Value!,false,60,comment,null);
            var saved=records.Length==0?await dns.CreateRecordAsync(key,zoneId,desired,c.RequestAborted):await dns.UpdateRecordAsync(key,zoneId,desired,c.RequestAborted);
            return Results.Ok(new{id=saved.Id});
        }).DisableAntiforgery();
    }
}
