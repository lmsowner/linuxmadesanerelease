// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.RemoteDesktop;
using LinuxMadeSane.Infrastructure.Services;
using Renci.SshNet;
namespace LinuxMadeSane.Web.Services.RemoteDesktop;

// View desktop shares an existing graphical session. No Linux login or saved password participates.
public sealed class LmsDesktopAccess(RemoteDesktopConnections access, IRemoteDesktopConnectionStore connections,
    IManagedHostStore hosts, ManagedHostSshConnectionFactory sshFactory, RemoteDesktopRuntime runtime,
    RemoteLmsSshTunnelService remote, IDesktopShareHost local, ILogger<LmsDesktopAccess> logger)
{
    public const string WaylandSharingInstructions = "On GNOME and other Wayland desktops, select the screen and click Share in the sharing prompt on the machine itself. LMS cannot connect until you approve. The desktop may remember this permission for future connections.";

    internal static Guid ConnectionId(Guid owner, Guid host) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"lms-desktop:{owner:N}:{host:N}"))[..16]);
    private async Task<ManagedHost> HostAsync(Guid id, CancellationToken token)
    {
        var host = await hosts.GetAsync(id, token) ?? (AiLocalMachine.IsLocalMachine(id) ? AiLocalMachine.CreateManagedHost() : null);
        if (host is null || !ManagedHostCapabilities.IsLmsHost(host)) throw new InvalidOperationException("Select a registered LMS host.");
        return host;
    }
    public async Task<Guid> PrepareAsync(Guid hostId, Func<string, Task> progress, CancellationToken token = default)
    {
        var owner = await access.UserAsync(token);
        var host = await HostAsync(hostId, token);
        await progress("Opening this machine’s desktop through VNC…");
        var target = AiLocalMachine.IsLocalMachine(hostId) ? await local.InspectAsync(token)
            : await remote.ShareDesktopAsync<DesktopShareDescriptor>(hostId, "inspect", token: token);
        var protocol = Protocol(target.Protocol);
        await progress($"{target.User}’s desktop is running. Preparing VNC…" +
            (target.DesktopType == "wayland" ? " " + WaylandSharingInstructions : ""));
        var engine = await runtime.StatusAsync(token);
        if (!engine.Healthy) await runtime.OperateAsync(engine.Installed ? "start" : "install", true, new ProgressSink(progress), token);
        if (!(await runtime.StatusAsync(token)).Healthy) throw new InvalidOperationException("The browser desktop viewer could not be started.");
        var id = ConnectionId(owner, hostId);
        var old = await connections.GetAsync(id, token);
        if (old is not null && (old.OwnerUserId != owner || old.HostId != hostId || !old.Options.ManagedLmsDesktop)) throw new UnauthorizedAccessException();
        var name = host.Name + " desktop";
        if (name.Length > 80) name = name[..80];
        if (old is null || old.Protocol != protocol || old.CredentialId != Guid.Empty || old.Options.ManagedDesktopSessionId != target.SessionId || old.Options.ManagedDesktopType != target.DesktopType)
            await connections.SaveAsync(new(id, owner, hostId, name, host.Hostname, protocol, 1, Guid.Empty,
                new() { ManagedLmsDesktop = true, ManagedDesktopSessionId = target.SessionId, ManagedDesktopType = target.DesktopType },
                (old?.Revision ?? 0) + 1, DateTimeOffset.UtcNow), token);
        await progress("Opening the running desktop. LMS is handling the sharing connection…" +
            (target.DesktopType == "wayland" ? " " + WaylandSharingInstructions : ""));
        return id;
    }
    public async Task<ManagedDesktopTransport> OpenTransportAsync(RemoteDesktopConnection connection, CancellationToken token)
    {
        if (!connection.Options.ManagedLmsDesktop || connection.HostId is not { } id || string.IsNullOrWhiteSpace(connection.Options.ManagedDesktopSessionId))
            throw new InvalidOperationException("Open this host using View desktop to share its current screen.");
        var host = await HostAsync(id, token);
        var lease = AiLocalMachine.IsLocalMachine(id) ? await local.StartAsync(connection.Options.ManagedDesktopSessionId, token)
            : await remote.ShareDesktopAsync<DesktopShareLease>(id, "start", connection.Options.ManagedDesktopSessionId, token: token);
        async Task Release()
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                if (AiLocalMachine.IsLocalMachine(id)) await local.StopAsync(lease, timeout.Token);
                else await remote.ShareDesktopAsync<JsonElement>(id, "stop", lease: lease, token: timeout.Token);
            }
            catch (Exception error) { logger.LogWarning("Desktop sharing cleanup could not reach host {HostId} ({FailureType}); its lease will expire.", id, error.GetType().Name); }
        }
        async Task<bool> Renew()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            return AiLocalMachine.IsLocalMachine(id) ? await local.RenewAsync(lease, timeout.Token)
                : (await remote.ShareDesktopAsync<JsonElement>(id, "renew", lease: lease, token: timeout.Token)).GetProperty("active").GetBoolean();
        }
        SshClient? ssh = null; ForwardedPortLocal? forward = null;
        try
        {
            if (Protocol(lease.Protocol) != connection.Protocol) throw new InvalidOperationException("The graphical session changed. Click View desktop again.");
            var network = await runtime.ManagedNetworkAsync(token);
            var port = lease.Port;
            if (!AiLocalMachine.IsLocalMachine(id))
            {
                var saved = await sshFactory.ResolveStoredCredentialsAsync(host, token);
                ssh = sshFactory.CreateSshClient(host, saved, TimeSpan.FromSeconds(12));
                await ssh.ConnectAsync(token);
                forward = new("127.0.0.1", 0, "127.0.0.1", (uint)port);
                ssh.AddForwardedPort(forward); forward.Start(); port = (int)forward.BoundPort;
            }
            return new(network.Gateway, network.Peer, port, ssh, forward, lease, Release, Renew);
        }
        catch { forward?.Dispose(); ssh?.Dispose(); await Release(); throw; }
    }
    private static RemoteDesktopProtocol Protocol(string protocol) => protocol switch
    { "vnc" => RemoteDesktopProtocol.Vnc, _ => throw new InvalidOperationException("Update the target LMS host to use VNC Quick View. RDP is not used by View desktop.") };
    private sealed class ProgressSink(Func<string, Task> callback) : IProgress<string>
    { public void Report(string value) => _ = callback(value); }
}
