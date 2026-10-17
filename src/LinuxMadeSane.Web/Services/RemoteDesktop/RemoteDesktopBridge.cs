// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RemoteDesktop;
using LinuxMadeSane.Infrastructure.Services.RemoteDesktop;
namespace LinuxMadeSane.Web.Services.RemoteDesktop;

public sealed class RemoteDesktopBridge(IServiceScopeFactory scopes, RemoteDesktopStorageSettings paths,
    RemoteDesktopSessionRegistry sessions, RemoteDesktopTrustedAccess trusted,
    ILogger<RemoteDesktopBridge> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1,1);
    private WebApplication? listener;
    private string key = "";
    public string SocketPath => Path.Combine(paths.RuntimeDirectory, "bridge.sock");
    internal string ServiceKey => key;
    public async Task EnsureListeningAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (listener is not null) return;
            if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("The remote desktop engine is installed on the Linux LMS host.");
            Directory.CreateDirectory(paths.ConfigurationDirectory); Directory.CreateDirectory(paths.RuntimeDirectory);
            File.SetUnixFileMode(paths.RuntimeDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var metadata = Path.Combine(paths.ConfigurationDirectory, "bridge.json");
            using var scope = scopes.CreateScope(); var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            var reference = File.Exists(metadata) ? JsonSerializer.Deserialize<BridgeMetadata>(await File.ReadAllTextAsync(metadata, token))?.SecretReference : null;
            key = !string.IsNullOrWhiteSpace(reference) ? await secrets.ResolveSecretAsync(reference, token) ?? "" : "";
            if (key.Length == 0)
            {
                key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                reference = await secrets.StoreSecretAsync(key, "Guacamole internal bridge service", token);
                await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(new BridgeMetadata(reference, "1.6.0")), token);
            }
            if (File.Exists(SocketPath)) File.Delete(SocketPath);
            var builder = WebApplication.CreateSlimBuilder();
            // No TCP listener, public route, separate login or identity store.
            builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(o => { o.ListenUnixSocket(SocketPath); o.Limits.MaxRequestBodySize = 4096; });
            var app = builder.Build();
            app.MapPost("/redeem", context => HandleAsync(context, redeem: true));
            app.MapPost("/validate", context => HandleAsync(context, redeem: false));
            await app.StartAsync(token);
            File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
            listener = app;
        }
        finally { gate.Release(); }
    }
    private async Task HandleAsync(HttpContext context, bool redeem)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        var expected = Encoding.UTF8.GetBytes("Bearer " + key); var supplied = Encoding.UTF8.GetBytes(authorization);
        if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected)) { context.Response.StatusCode = 401; return; }
        try
        {
            var request = await context.Request.ReadFromJsonAsync<BridgeRequest>(context.RequestAborted) ?? throw new UnauthorizedAccessException();
            var session = redeem ? sessions.Consume(request.Grant ?? "", request.UserId, request.ConnectionId, request.SessionId) : sessions.Get(request.SessionId);
            using var scope = scopes.CreateScope();
            if (session is null || !await scope.ServiceProvider.GetRequiredService<RemoteDesktopAuthorizer>().ValidAsync(session, context.RequestAborted)) throw new UnauthorizedAccessException();
            object response;
            if (!redeem) response = new { valid = true };
            else
            {
                var connection = session.Connection;
                var target = await RemoteDesktopConnections.ResolveTargetAsync(connection.Hostname, context.RequestAborted);
                using var access = trusted.Enter(connection.OwnerUserId);
                var credential = await scope.ServiceProvider.GetRequiredService<ISavedConnectionCredentialService>().ResolveAsync(connection.OwnerUserId, connection.CredentialId, context.RequestAborted) ?? throw new UnauthorizedAccessException();
                if (credential.Summary.Kind != RemoteDesktopConnections.CredentialKind(connection.Protocol) || string.IsNullOrEmpty(credential.Password)) throw new UnauthorizedAccessException();
                var parameters = BuildParameters(connection, session.Id, target.ToString(), credential);
                var user = connection.OwnerUserId == Guid.Empty ? null : await scope.ServiceProvider.GetRequiredService<ISecurityUserStore>().GetAsync(connection.OwnerUserId, context.RequestAborted) ?? throw new UnauthorizedAccessException();
                response = new { protocol = connection.Protocol == RemoteDesktopProtocol.Rdp ? "rdp" : "vnc", parameters, userLabel = user?.Email ?? "Trusted LMS administrator" };
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
            context.Response.ContentType = "application/json"; context.Response.ContentLength = bytes.Length;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or JsonException or System.Net.Sockets.SocketException)
        {
            // Never log exception bodies: backend failures could include protocol data.
            logger.LogWarning("Remote desktop bridge request rejected ({FailureType}).", ex.GetType().Name);
            context.Response.StatusCode = 403;
        }
    }
    internal static Dictionary<string,string> BuildParameters(RemoteDesktopConnection c, Guid session, string target, LinuxMadeSane.Application.Contracts.SavedConnectionCredential credential)
    {
        string Flag(bool flag) => flag ? "true" : "false";
        var o = c.Options;
        var p = new Dictionary<string,string> { ["hostname"] = target, ["port"] = c.Port.ToString(), ["password"] = credential.Password,
            ["color-depth"] = o.ColourDepth.ToString(), ["disable-copy"] = Flag(!o.Clipboard), ["disable-paste"] = Flag(!o.Clipboard || o.ReadOnly) };
        if (c.Protocol == RemoteDesktopProtocol.Rdp)
        {
            p["username"] = credential.Summary.Username; p["domain"] = credential.Summary.Domain;
            p["security"] = o.SecurityMode; p["ignore-cert"] = Flag(o.AcceptUntrustedCertificate);
            p["width"] = o.Width.ToString(); p["height"] = o.Height.ToString(); p["resize-method"] = o.ResizeMethod;
            p["disable-audio"] = Flag(!o.Audio); p["enable-drive"] = Flag(o.Drive);
            if (o.Drive) { p["drive-path"] = "/tmp/lms-rd/" + session.ToString("N"); p["create-drive-path"] = "true"; p["drive-name"] = "LMS temporary files"; }
        }
        else p["read-only"] = Flag(o.ReadOnly);
        return p;
    }
    public async ValueTask DisposeAsync() { if (listener is not null) { await listener.StopAsync(); await listener.DisposeAsync(); } gate.Dispose(); }
    private sealed record BridgeMetadata(string SecretReference, string Version);
    private sealed record BridgeRequest(Guid SessionId, Guid UserId, Guid ConnectionId, string? Grant);
}
