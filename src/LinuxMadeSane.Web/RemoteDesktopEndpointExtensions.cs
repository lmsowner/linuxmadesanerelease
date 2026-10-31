// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Net;
using System.Net.WebSockets;
using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.RemoteDesktop;
using LinuxMadeSane.Core.Versioning;
using LinuxMadeSane.Web.Services.RemoteDesktop;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.WebUtilities;
namespace LinuxMadeSane.Web;

public static class RemoteDesktopEndpointExtensions
{
    // Same-origin form navigations must retain Origin for the Portal CSRF guard.
    internal const string ReferrerPolicy = "same-origin";
    internal static Guid RequestUserId(HttpContext context)
    {
        if(context.User.Identity?.IsAuthenticated == true) return RemoteDesktopAuthorizer.UserId(context.User);
        if(context.Items["LmsTrustedNetworkAccess"] is TrustedNetworkAccessResult {IsTrusted:true}) return Guid.Empty;
        throw new UnauthorizedAccessException("LMS trusted access or an authenticated session is required.");
    }
    private static async ValueTask<object?> AuthorizeRequestAsync(EndpointFilterInvocationContext invocation,EndpointFilterDelegate next)
    {
        try { RequestUserId(invocation.HttpContext); }
        catch(UnauthorizedAccessException) { return Results.Unauthorized(); }
        return await next(invocation);
    }
    public static string CookieBinding(HttpContext context) => RemoteDesktopSessionRegistry.Hash(
        context.User.Identity?.IsAuthenticated == true
        ? (new ChunkingCookieManager().GetRequestCookie(context,"lms.remote") ?? "") + "|" + RequestUserId(context)
        : (context.Request.Cookies["lms.desktop-binding"] ?? "") + "|trusted|" + context.Connection.RemoteIpAddress + "|" + context.Request.Host.Host);
    public static void RevokeRemoteDesktops(HttpContext context) => RevokeRemoteDesktops(context,null);
    public static void RevokeRemoteDesktops(HttpContext context, System.Security.Claims.ClaimsPrincipal? rejectedPrincipal)
    {
        var sessions=context.RequestServices.GetRequiredService<RemoteDesktopSessionRegistry>();
        string binding;
        if(rejectedPrincipal is not null)
        {
            // Cookie validation runs before AuthenticationMiddleware assigns HttpContext.User.
            // Use the rejected ticket solely to revoke its existing exact cookie binding.
            if(!Guid.TryParse(rejectedPrincipal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,out var owner) || owner==Guid.Empty)return;
            binding=RemoteDesktopSessionRegistry.Hash((new ChunkingCookieManager().GetRequestCookie(context,"lms.remote") ?? "")+"|"+owner);
        }
        else
        {
            try {binding=CookieBinding(context);}
            catch(UnauthorizedAccessException) {return;}
        }
        foreach(var session in sessions.Active.Where(s=>s.CookieBinding==binding))sessions.Revoke(session);
    }
    public static void MapRemoteDesktop(this WebApplication app)
    {
        app.MapGet("/remote-desktop/launch/{connectionId:guid}", async (HttpContext context, Guid connectionId, IRemoteDesktopConnectionStore store, IAntiforgery antiforgery) =>
        {
            if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site") return Results.StatusCode(403);
            var user = RequestUserId(context); var connection = await store.GetAsync(connectionId,context.RequestAborted);
            if (connection is null || connection.OwnerUserId != user) return Results.NotFound();
            if (user == Guid.Empty && !context.Request.Cookies.ContainsKey("lms.desktop-binding"))
                context.Response.Cookies.Append("lms.desktop-binding",Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),new CookieOptions {HttpOnly=true,Secure=context.Request.IsHttps,SameSite=SameSiteMode.Strict,Path="/",MaxAge=TimeSpan.FromHours(8)});
            var token = antiforgery.GetAndStoreTokens(context); SetPrivateHeaders(context);
            var assetVersion = Uri.EscapeDataString(LinuxMadeSaneBuildVersion.GetCurrent(typeof(RemoteDesktopEndpointExtensions).Assembly));
            var html = "<!doctype html><html><head><meta charset=utf-8><title>LMS - Remote Desktop - " + WebUtility.HtmlEncode(connection.Name) +
                "</title><meta name=referrer content="+ReferrerPolicy+"></head><body><p>Preparing remote desktop…</p><form id=remote-desktop-launch method=post action=\"/remote-desktop/launch\">" +
                "<input type=hidden name=connectionId value=\"" + connection.Id + "\"><input type=hidden name=\"" + WebUtility.HtmlEncode(token.FormFieldName) + "\" value=\"" +
                WebUtility.HtmlEncode(token.RequestToken) + "\"><button type=submit>Open remote desktop</button></form><script src=\"/scripts/remote-desktop-launch.js?v="+assetVersion+"\"></script></body></html>";
            return Results.Content(html,"text/html");
        }).AddEndpointFilter(AuthorizeRequestAsync);
        app.MapPost("/remote-desktop/launch", async (HttpContext context, IAntiforgery antiforgery,
            IRemoteDesktopConnectionStore connections, ISecurityUserStore users, RemoteDesktopSessionRegistry sessions, RemoteDesktopRuntime runtime, RemoteDesktopConnections editor) =>
        {
            SetPrivateHeaders(context);
            RemoteDesktopSession? session = null; var stage = "Validate launch"; long? savedRevision=null;
            var jsonResponse=context.Request.Headers.Accept.ToString().Contains("application/json",StringComparison.OrdinalIgnoreCase);
            IResult LaunchError(string error,int status=409)=>jsonResponse ? Results.Json(new {error,connectionRevision=savedRevision},statusCode:status) : Error(error);
            try
            {
                await antiforgery.ValidateRequestAsync(context);
                var userId = RequestUserId(context);
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                if (!Guid.TryParse(form["connectionId"],out var id)) return LaunchError("Invalid connection.",400);
                var connection = await connections.GetAsync(id,context.RequestAborted);
                var user = userId == Guid.Empty ? null : await users.GetAsync(userId,context.RequestAborted);
                if (connection is null || connection.OwnerUserId != userId || userId != Guid.Empty && user?.IsEnabled != true) return LaunchError("This connection is unavailable.",404);
                if (!(await runtime.StatusAsync(context.RequestAborted)).Healthy) return LaunchError("The remote desktop engine is not ready. Return to Hosts > Remote Desktop (RDP/VNC) to install or start it.");
                var authentication = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                var expires = DateTimeOffset.UtcNow.AddHours(8);
                if (userId != Guid.Empty)
                {
                    if (!authentication.Succeeded || authentication.Properties?.IssuedUtc is not { } issued) return LaunchError("Sign in again before reconnecting.",401);
                    expires=issued.AddMinutes(SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(user!.SessionLifetimeMinutes));
                    if(authentication.Properties.ExpiresUtc is { } cookieExpires && cookieExpires < expires) expires=cookieExpires;
                }
                else if(string.IsNullOrEmpty(context.Request.Cookies["lms.desktop-binding"])) return LaunchError("The local desktop binding is missing. Open Hosts separately and Connect again.",401);
                if (form["reconnect"] == "true")
                {
                    if (!long.TryParse(form["connectionRevision"],out var revision) || revision != connection.Revision)
                        return LaunchError("This connection changed in another window. Open a fresh connection from Hosts before applying settings.");
                    RemoteDesktopSession? previous = null;
                    if (Guid.TryParse(form["sessionId"],out var previousId))
                        previous = sessions.GetOwned(previousId,userId,CookieBinding(context));
                    if (previous is not null && previous.Connection.Id != connection.Id) return LaunchError("Invalid reconnect.",400);
                    var updated = ApplyReconnectOptions(connection,form);
                    // Reuse saved-connection validation, secret references and revocation.
                    if (updated.Options != connection.Options)
                    {
                        stage="Save reconnect settings";
                        using var requestAccess=context.RequestServices.GetRequiredService<RemoteDesktopTrustedAccess>().Enter(userId);
                        connection = await editor.SaveAsync(updated,context.RequestAborted);
                        savedRevision=connection.Revision;
                    }
                    if (previous is not null)
                    {
                        sessions.Revoke(previous);
                        await runtime.CleanupAsync(previous,context.RequestAborted);
                    }
                }
                stage = "Create bound session";
                session = sessions.Create(connection,CookieBinding(context),expires,user?.UpdatedAtUtc,
                    userId == Guid.Empty ? context.Connection.RemoteIpAddress?.ToString() : null,userId == Guid.Empty ? context.Request.Host.Host : null);
                stage = "Bootstrap Guacamole";
                await runtime.BootstrapAsync(session,sessions.Issue(session),context.RequestAborted);
                return jsonResponse ? Results.Json(new {sessionUrl=$"/remote-desktop/session/{session.Id}"}) : Results.Redirect($"/remote-desktop/session/{session.Id}");
            }
            catch (AntiforgeryValidationException) { return LaunchError("This launch expired. Sign in again and Connect from Hosts.",400); }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException)
            { context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("LMS.RemoteDesktop").LogWarning("Remote desktop launch failed at {Stage}: {FailureType}; {CallPath}",stage,ex.GetType().Name,ex.StackTrace); if (session is not null) sessions.Revoke(session); return LaunchError($"Remote desktop could not be opened during {stage}. Check engine health, target credentials and your LMS session, then retry Reconnect."); }
        }).AddEndpointFilter(AuthorizeRequestAsync);
        app.MapGet("/remote-desktop/session/{sessionId:guid}", async (HttpContext context, Guid sessionId, RemoteDesktopSessionRegistry sessions, RemoteDesktopAuthorizer authorizer, IAntiforgery antiforgery) =>
        {
            var session = sessions.GetOwned(sessionId,RequestUserId(context),CookieBinding(context));
            if (session is null || !await authorizer.ValidAsync(session,context.RequestAborted)) return Error("This remote desktop session ended. Connect again from Hosts.");
            SetPrivateHeaders(context); var token = antiforgery.GetAndStoreTokens(context);
            var assetVersion = Uri.EscapeDataString(LinuxMadeSaneBuildVersion.GetCurrent(typeof(RemoteDesktopEndpointExtensions).Assembly));
            var client = Convert.ToBase64String(Encoding.UTF8.GetBytes(session.Connection.Id+"\0c\0lms"));
            return Results.Content("<!doctype html><html><head><meta charset=utf-8><title>LMS - Remote Desktop - " + WebUtility.HtmlEncode(session.Connection.Name) +
                "</title><meta name=referrer content="+ReferrerPolicy+"><link rel=stylesheet href=\"/styles/theme.css\"><link rel=stylesheet href=\"/styles/ui.css\"><link rel=stylesheet href=\"/styles/remote-desktop.css?v="+assetVersion+"\"></head><body class=remote-desktop-viewer data-clipboard=\""+session.Connection.Options.Clipboard.ToString().ToLowerInvariant()+"\" data-clipboard-paste=\""+(session.Connection.Options.Clipboard && !session.Connection.Options.ReadOnly).ToString().ToLowerInvariant()+"\" data-session-state=\"/remote-desktop/session/" + session.Id + "/state\">" +
                BuildViewerToolbar(session.Connection,session.Id,token) +
                "<section class=remote-desktop-clipboard hidden><textarea aria-label=\"Remote clipboard text\" rows=3 spellcheck=false></textarea><div><button type=button data-clipboard-paste>Paste from device</button><button type=button data-clipboard-send>Send to remote</button><button type=button data-clipboard-copy>Copy from remote</button><span role=status></span></div></section>" +
                "<p class=remote-desktop-load-status role=status>Loading remote desktop…</p>" + (session.Connection.OwnerUserId == Guid.Empty ? "" : "<a class=remote-desktop-signin hidden href=\"/LMSMFALogin?returnUrl=%2Fhosts%3Ftab%3Dremote-desktop\" target=\"_blank\" rel=\"noopener noreferrer\">Sign in again, then Connect from Hosts</a>") + "<iframe title=\"Remote desktop\" referrerpolicy=no-referrer allow=\"fullscreen; clipboard-read; clipboard-write\" allowfullscreen data-src=\"/remote-desktop/client/"+session.Id+"/#/client/"+Uri.EscapeDataString(client)+"\"></iframe><script src=\"/scripts/remote-desktop-launch.js?v="+assetVersion+"\"></script><script src=\"/scripts/remote-desktop-viewer.js?v="+assetVersion+"\"></script><script src=\"/scripts/remote-desktop-clipboard.js?v="+assetVersion+"\"></script></body></html>","text/html");
        }).AddEndpointFilter(AuthorizeRequestAsync);
        app.MapGet("/remote-desktop/session/{sessionId:guid}/state", async (HttpContext context, Guid sessionId, RemoteDesktopSessionRegistry sessions, RemoteDesktopAuthorizer authorizer) =>
        {
            SetPrivateHeaders(context);
            var session=sessions.GetOwned(sessionId,RequestUserId(context),CookieBinding(context));
            if(session is null || !await authorizer.ValidAsync(session,context.RequestAborted))return Results.Json(new {active=false,authenticated=RequestUserId(context)!=Guid.Empty},statusCode:410);
            return Results.Json(new {active=true,expiresAtUtc=session.Expires,authenticated=RequestUserId(context)!=Guid.Empty});
        }).AddEndpointFilter(AuthorizeRequestAsync);
        app.MapPost("/remote-desktop/session/{sessionId:guid}/disconnect", async (HttpContext context, Guid sessionId, IAntiforgery antiforgery, RemoteDesktopSessionRegistry sessions, RemoteDesktopRuntime runtime) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest("Invalid disconnect request."); }
            var session = sessions.GetOwned(sessionId,RequestUserId(context),CookieBinding(context));
            if (session is not null) { sessions.Revoke(session); await runtime.CleanupAsync(session,context.RequestAborted); }
            SetPrivateHeaders(context);
            return Results.Content("<!doctype html><html><head><meta charset=utf-8><title>LMS - Remote Desktop - Disconnected</title>" +
                "<meta name=referrer content="+ReferrerPolicy+"><link rel=stylesheet href=\"/styles/theme.css\"></head><body>" +
                "<p role=status>Remote desktop disconnected. You can close this window.</p>" +
                "<a href=\"/hosts?tab=remote-desktop\" target=\"_blank\" rel=\"noopener noreferrer\">Return to Hosts</a>" +
                "<script src=\"/scripts/remote-desktop-disconnected.js\"></script></body></html>", "text/html");
        }).AddEndpointFilter(AuthorizeRequestAsync);
        app.MapMethods("/remote-desktop/client/{sessionId:guid}/{**path}",["GET","HEAD","POST","PUT","DELETE","CONNECT"], ProxyAsync).AddEndpointFilter(AuthorizeRequestAsync);
    }
    internal static RemoteDesktopConnection ApplyReconnectOptions(
        RemoteDesktopConnection connection,IFormCollection form)
    {
        bool Flag(string name)
        {
            var value=form[name].ToString();
            if(value is not ("" or "true"))throw new InvalidOperationException("Invalid reconnect option.");
            return value=="true";
        }
        if(!int.TryParse(form["colourDepth"],out var depth))throw new InvalidOperationException("Invalid colour depth.");
        var rdp=connection.Protocol==RemoteDesktopProtocol.Rdp;
        var result=connection with {Options=connection.Options with {
            SecurityMode=rdp ? form["securityMode"].ToString() : connection.Options.SecurityMode,
            AcceptUntrustedCertificate=rdp ? Flag("acceptUntrustedCertificate") : connection.Options.AcceptUntrustedCertificate,
            ColourDepth=depth, Clipboard=Flag("clipboard"),
            Audio=rdp ? Flag("audio") : connection.Options.Audio,
            ReadOnly=rdp ? connection.Options.ReadOnly : Flag("readOnly"),
            VncEncoding=rdp || !form.ContainsKey("vncEncoding") ? connection.Options.VncEncoding : form["vncEncoding"].ToString(),
            VncCopyRect=rdp || !form.ContainsKey("vncEncoding") ? connection.Options.VncCopyRect : Flag("vncCopyRect"),
            VncLossless=rdp || !form.ContainsKey("vncEncoding") ? connection.Options.VncLossless : Flag("vncLossless")
        }};
        RemoteDesktopConnections.Validate(result);
        return result;
    }
    private static string BuildViewerToolbar(RemoteDesktopConnection connection,
        Guid sessionId,AntiforgeryTokenSet token)
    {
        string Hidden(string name,string? value)=>"<input type=hidden name=\""+WebUtility.HtmlEncode(name)+"\" value=\""+WebUtility.HtmlEncode(value)+"\">";
        string Choice(string value,string label,string current)=>"<option value=\""+value+"\""+(value==current ? " selected" : "")+">"+label+"</option>";
        string Flag(string name,string label,bool value,string? description=null)=>"<label class=remote-desktop-checkbox title=\""+WebUtility.HtmlEncode(description ?? label)+"\"><input type=checkbox name=\""+name+"\" aria-label=\""+WebUtility.HtmlEncode(description ?? label)+"\" value=true"+(value ? " checked" : "")+">"+label+"</label>";
        var o=connection.Options; var rdp=connection.Protocol==RemoteDesktopProtocol.Rdp;
        var settings=new StringBuilder();
        if(rdp)
        {
            settings.Append("<span class=remote-desktop-option><label for=desktop-security>Authentication</label><select title=\"Legacy RDP uses weaker security; choose it only for a target that requires it.\" class=ui-control-input id=desktop-security name=securityMode>");
            foreach(var (value,label) in new[]{("nla","Network (NLA)"),("tls","TLS"),("rdp","Legacy RDP"),("any","Negotiate")})settings.Append(Choice(value,label,o.SecurityMode));
            settings.Append("</select></span>").Append(Flag("acceptUntrustedCertificate","Self-signed cert",o.AcceptUntrustedCertificate,"Accept this target’s untrusted/self-signed certificate"));
        }
        if (!rdp)
        {
            settings.Append("<span class=remote-desktop-option><label for=desktop-encoding>Encoding</label><select class=ui-control-input id=desktop-encoding name=vncEncoding>");
            foreach(var (value,label) in new[]{("auto","Auto"),("zrle","ZRLE"),("hextile","Hextile"),("raw","Raw")})settings.Append(Choice(value,label,o.VncEncoding));
            settings.Append("</select></span>")
                .Append(Flag("vncCopyRect","CopyRect",o.VncCopyRect,"Reuse screen regions when moving windows. Turn off to test ghosting."))
                .Append(Flag("vncLossless","Lossless",o.VncLossless,"Use lossless browser graphics. May increase bandwidth."));
        }
        settings.Append("<span class=remote-desktop-option><label for=desktop-colour>Colour depth</label><select class=ui-control-input id=desktop-colour name=colourDepth>");
        foreach(var depth in new[]{8,16,24,32})settings.Append(Choice(depth.ToString(),depth+" bit",o.ColourDepth.ToString()));
        settings.Append("</select></span>").Append(Flag("clipboard","Clipboard",o.Clipboard));
        settings.Append(rdp ? Flag("audio","Audio",o.Audio) : Flag("readOnly","Read only",o.ReadOnly));
        return "<header><span class=remote-desktop-host>LMS · "+WebUtility.HtmlEncode(connection.Name)+"</span>"+
            "<form class=remote-desktop-controls method=post action=\"/remote-desktop/launch\">"+
            Hidden("connectionId",connection.Id.ToString())+Hidden("sessionId",sessionId.ToString())+Hidden("connectionRevision",connection.Revision.ToString())+Hidden("reconnect","true")+Hidden(token.FormFieldName,token.RequestToken)+
            settings.ToString()+
            "<button class=\"glass-button-toolbar ui-button ui-button-sm\" type=button data-desktop-clipboard>Copy / Paste</button>"+
            "<button class=\"glass-button-toolbar ui-button ui-button-sm\" type=submit value=reconnect title=\"Save these settings and reconnect. Changing settings closes other sessions for this connection.\">Reconnect</button>"+
            "<button class=\"glass-button-toolbar ui-button ui-button-sm\" type=submit formnovalidate formaction=\"/remote-desktop/session/"+sessionId+"/disconnect\">Disconnect</button><span class=remote-desktop-settings-status role=status hidden>Settings changed · Reconnect to apply and save</span></form></header>";
    }
    private static IResult Error(string message) => Results.Content("<!doctype html><html><head><meta charset=utf-8><title>LMS - Remote Desktop</title></head><body><p>"+WebUtility.HtmlEncode(message)+"</p><a href=\"/hosts?tab=remote-desktop\" target=\"_blank\" rel=\"noopener noreferrer\">Return to Remote Desktop</a></body></html>","text/html",statusCode:409);
    private static void SetPrivateHeaders(HttpContext context, bool guacamole = false)
    {
        context.Response.Headers.CacheControl = "no-store"; context.Response.Headers["Referrer-Policy"] = ReferrerPolicy;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'" + (guacamole ? " 'unsafe-eval'" : "") + "; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self' ws: wss:; font-src 'self' data:; media-src 'self' blob:; frame-src 'self'; frame-ancestors 'self'; form-action 'self'; base-uri 'self'";
    }
    internal static bool AllowedPath(string path, string method, string? username = null)
    {
        if (path.Split('/').Any(s => s == "..") || path.Contains('\\') || path.Contains(':') || path.Contains('%') || path.Contains(';') || path.Contains('?') || path.Contains('#') || path.StartsWith('/') || path.Any(char.IsControl)) return false;
        if (path == "websocket-tunnel") return method is "GET" or "CONNECT";
        if (!path.StartsWith("api/",StringComparison.Ordinal)) return method == "GET" || path == "tunnel" && method == "POST";
        if (path.Contains("/parameters",StringComparison.Ordinal) || path.Contains("/sharingCredentials",StringComparison.Ordinal)) return false;
        if (path == "api/tokens" && method == "POST" || path == "api/tokens/lms" && method == "DELETE") return true;
        if (path == "api/session") return method == "HEAD";
        if (path == "api/languages" || path == "api/patches") return method == "GET";
        if (path.StartsWith("api/session/tunnels/",StringComparison.Ordinal)) return true;
        if (method == "GET" && (path == "api/session/data/lms/self" || path.StartsWith("api/session/data/lms/self/",StringComparison.Ordinal))) return true;
        if (method == "GET" && username is not null && (path == "api/session/data/lms/users/" + username || path == "api/session/data/lms/users/" + username + "/permissions")) return true;
        if (method != "GET" || path.Contains("/parameters",StringComparison.Ordinal) || path.Contains("/users",StringComparison.Ordinal) || path.Contains("/permissions",StringComparison.Ordinal)) return false;
        return path.StartsWith("api/session/data/lms/connections",StringComparison.Ordinal) ||
            path.StartsWith("api/session/data/lms/connectionGroups",StringComparison.Ordinal) || path.StartsWith("api/session/data/lms/activeConnections",StringComparison.Ordinal);
    }
    private static async Task ProxyAsync(HttpContext context, Guid sessionId, string? path,
        RemoteDesktopSessionRegistry sessions, RemoteDesktopAuthorizer authorizer, RemoteDesktopRuntime runtime, IHttpClientFactory clients)
    {
        path ??= ""; SetPrivateHeaders(context, guacamole: true);
        if(context.Request.Method=="CONNECT" && !context.WebSockets.IsWebSocketRequest) {context.Response.StatusCode=400;return;}
        if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site") { context.Response.StatusCode=403; return; }
        var session = sessions.GetOwned(sessionId,RequestUserId(context),CookieBinding(context));
        if (session is null || !await authorizer.ValidAsync(session,context.RequestAborted)) { context.Response.StatusCode=401; return; }
        session.Touch();
        using var browserIdentity = System.Text.Json.JsonDocument.Parse(session.BrowserAuthJson);
        var username = browserIdentity.RootElement.GetProperty("username").GetString();
        if (!AllowedPath(path,context.Request.Method,username)) { context.Response.StatusCode=404; return; }
        var origin = context.Request.Headers.Origin.ToString(); var expectedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        if ((context.WebSockets.IsWebSocketRequest || context.Request.Method != "GET" && !(path == "api/session" && context.Request.Method == "HEAD")) && !origin.Equals(expectedOrigin,StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode=403; return; }
        if (path.EndsWith("/sharingProfiles",StringComparison.Ordinal) && context.Request.Method == "GET")
        { context.Response.ContentType="application/json"; await context.Response.WriteAsync("{}",context.RequestAborted); return; }
        if (path == "api/tokens")
        {
            try { await runtime.RefreshAuthenticationAsync(session, context.RequestAborted); context.Response.ContentType="application/json"; await context.Response.WriteAsync(session.BrowserAuthJson,context.RequestAborted); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) { sessions.Revoke(session); context.Response.StatusCode=401; }
            return;
        }
        if (path == "api/tokens/lms") { sessions.Revoke(session); await runtime.CleanupAsync(session,context.RequestAborted); context.Response.StatusCode=204; return; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,session.Revoked.Token);
        var query = context.Request.Query.Where(p => p.Key != "token").ToDictionary(p => p.Key,p => p.Value.ToString());
        // Tunnel APIs use query/form tokens; API requests also use the supported token header.
        if (path == "websocket-tunnel") query["token"] = session.GuacamoleToken;
        var uri = new Uri(QueryHelpers.AddQueryString(new Uri(runtime.EngineUri,path).AbsoluteUri,query!));
        if (path == "tunnel")
        {
            var operation = context.Request.QueryString.Value?.TrimStart('?') ?? "";
            if (!AllowedHttpTunnelOperation(operation)) { context.Response.StatusCode=400; return; }
            // This is a servlet operation string, not a key/value query. Preserve it.
            uri = new Uri(new Uri(runtime.EngineUri,path).AbsoluteUri + "?" + operation);
        }
        try
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                if (path != "websocket-tunnel") { context.Response.StatusCode=404; return; }
                using var upstream = new ClientWebSocket(); upstream.Options.AddSubProtocol("guacamole");
                var websocketUri = new UriBuilder(uri) { Scheme = "ws" }.Uri;
                await upstream.ConnectAsync(websocketUri,linked.Token);
                using var browser = await context.WebSockets.AcceptWebSocketAsync("guacamole");
                var outgoing = RelayAsync(browser,upstream,linked.Token); var incoming=RelayAsync(upstream,browser,linked.Token);
                await Task.WhenAny(outgoing,incoming); linked.Cancel(); browser.Abort(); upstream.Abort();
                await Task.WhenAll(outgoing,incoming).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                sessions.Revoke(session); return;
            }
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),uri);
            request.Headers.TryAddWithoutValidation("Guacamole-Token",session.GuacamoleToken);
            if (path == "tunnel" && !string.IsNullOrEmpty(session.HttpTunnelToken)) request.Headers.TryAddWithoutValidation("Guacamole-Tunnel-Token",session.HttpTunnelToken);
            if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
            {
                if (path == "tunnel" && context.Request.HasFormContentType)
                {
                    var form = await context.Request.ReadFormAsync(linked.Token);
                    var fields = form.ToDictionary(f => f.Key,f => f.Value.ToString()); fields["token"]=session.GuacamoleToken;
                    request.Content = new FormUrlEncodedContent(fields);
                }
                else
                {
                    request.Content = new StreamContent(context.Request.Body);
                    if (context.Request.ContentType is { } type) request.Content.Headers.TryAddWithoutValidation("Content-Type",type);
                }
            }
            using var response = await clients.CreateClient("LmsRemoteDesktop").SendAsync(request,HttpCompletionOption.ResponseHeadersRead,linked.Token);
            context.Response.StatusCode=(int)response.StatusCode;
            if (path == "tunnel" && response.Headers.TryGetValues("Guacamole-Tunnel-Token",out var tunnelTokens))
            {
                session.HttpTunnelToken=tunnelTokens.Single();
                context.Response.Headers["Guacamole-Tunnel-Token"]="lms";
            }
            foreach (var header in response.Content.Headers.Where(h => h.Key is "Content-Type" or "Content-Encoding" or "Content-Length")) context.Response.Headers[header.Key]=header.Value.ToArray();
            if (response.IsSuccessStatusCode && path.EndsWith(".css",StringComparison.OrdinalIgnoreCase)) context.Response.ContentType="text/css";
            if (response.Headers.TryGetValues("Guacamole-Status-Code",out var codes)) context.Response.Headers["Guacamole-Status-Code"]=codes.ToArray();
            if (response.Headers.TryGetValues("Guacamole-Error-Message",out var errors)) context.Response.Headers["Guacamole-Error-Message"]=errors.ToArray();
            await response.Content.CopyToAsync(context.Response.Body,linked.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or WebSocketException or OperationCanceledException or IOException)
        {
            // Guacamole normally aborts each completed HTTP read before handing
            // over to its already queued reader. This does not revoke the session.
            var normalReadCancellation = path == "tunnel" && context.Request.QueryString.Value?.StartsWith("?read:",StringComparison.Ordinal) == true && context.RequestAborted.IsCancellationRequested;
            if (!normalReadCancellation) sessions.Revoke(session);
            if (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested) { context.Response.StatusCode=502; await context.Response.WriteAsync("Remote desktop interrupted. Check the engine and target, then Connect again from Hosts."); }
        }
    }
    internal static bool AllowedHttpTunnelOperation(string operation) => operation == "connect" ||
        System.Text.RegularExpressions.Regex.IsMatch(operation,"^(read|write):[a-fA-F0-9-]{36}(:[0-9]+)?$");
    private static async Task RelayAsync(WebSocket source, WebSocket destination, CancellationToken token)
    {
        var buffer=new byte[65536];
        while (!token.IsCancellationRequested)
        {
            var result=await source.ReceiveAsync(buffer,token);
            if (result.MessageType==WebSocketMessageType.Close) return;
            await destination.SendAsync(buffer.AsMemory(0,result.Count),result.MessageType,result.EndOfMessage,token);
        }
    }
}
