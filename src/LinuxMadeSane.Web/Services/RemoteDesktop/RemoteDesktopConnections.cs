// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Sockets;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RemoteDesktop;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Application.Contracts;
namespace LinuxMadeSane.Web.Services.RemoteDesktop;

public sealed class RemoteDesktopConnections(IRemoteDesktopConnectionStore store, IManagedHostStore hosts,
    ISavedConnectionCredentialService credentials, ISavedCredentialAccessContext access,
    ISecurityUserStore users, RemoteDesktopSessionRegistry registry, LinuxMadeSane.Infrastructure.Persistence.LinuxMadeSaneDbContext db, RemoteDesktopDiscoveryJobs? discoveryJobs = null)
{
    public async Task<Guid> UserAsync(CancellationToken token = default)
    {
        var id = await access.GetAuthenticatedUserIdAsync(token);
        if (id is null || id != Guid.Empty && (await users.GetAsync(id.Value, token))?.IsEnabled != true)
            throw new UnauthorizedAccessException("LMS trusted access or an authenticated session is required.");
        return id.Value;
    }
    public async Task<IReadOnlyList<RemoteDesktopConnection>> ListAsync(CancellationToken token = default) => await store.ListAsync(await UserAsync(token), token);
    public async Task<RemoteDesktopConnection> SaveAsync(RemoteDesktopConnection item, CancellationToken token = default)
    {
        var user = await UserAsync(token);
        var old = await store.GetAsync(item.Id, token);
        if (old is not null && old.OwnerUserId != user) throw new UnauthorizedAccessException();
        Validate(item);
        var credential = (await credentials.ListAsync(user, token)).FirstOrDefault(c => c.Id == item.CredentialId);
        if (credential is null || !credential.HasPassword || credential.Kind != CredentialKind(item.Protocol))
            throw new InvalidOperationException("Choose an existing target credential for this protocol. Missing restored credentials must be configured again.");
        var inventory = await hosts.ListAsync(token);
        var associated = item.HostId.HasValue ? inventory.FirstOrDefault(h => h.Id == item.HostId) :
            inventory.FirstOrDefault(h => h.Hostname.TrimEnd('.').Equals(item.Hostname.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
        if (item.HostId.HasValue && associated is null) throw new InvalidOperationException("The associated LMS host no longer exists.");
        var saved = item with { OwnerUserId = user, Name = item.Name.Trim(), Hostname = item.Hostname.Trim().TrimEnd('.'),
            HostId = associated?.Id, Revision = (old?.Revision ?? 0) + 1, UpdatedAtUtc = DateTimeOffset.UtcNow };
        await store.SaveAsync(saved, token);
        foreach (var session in registry.Active.Where(s => s.Connection.Id == saved.Id)) registry.Revoke(session);
        return saved;
    }
    public async Task<HashSet<Guid>> AvailableCredentialIdsAsync(CancellationToken token = default)
    {
        await UserAsync(token);
        return (await db.SavedConnectionCredentials.AsNoTracking().Where(c => c.PasswordReference != null &&
            db.ProtectedSecrets.Any(s => s.Reference == c.PasswordReference)).Select(c => c.Id).ToArrayAsync(token)).ToHashSet();
    }
    public async Task DeleteAsync(Guid id, CancellationToken token = default)
    {
        var user = await UserAsync(token); var item = await store.GetAsync(id, token);
        if (item is not null && item.OwnerUserId != user) throw new UnauthorizedAccessException();
        await store.DeleteAsync(id, token);
        foreach (var session in registry.Active.Where(s => s.Connection.Id == id)) registry.Revoke(session);
    }
    public async Task<string> TestAsync(RemoteDesktopConnection connection, CancellationToken token = default)
    {
        var user = await UserAsync(token); var saved = await store.GetAsync(connection.Id, token);
        if (saved is null || saved.OwnerUserId != user) throw new UnauthorizedAccessException();
        return await ProbeAsync(saved.Hostname,saved.Port,saved.Protocol,token);
    }
    public async Task<string> TestDraftAsync(RemoteDesktopConnection draft,CancellationToken token=default)
    {
        var user=await UserAsync(token);
        var old=await store.GetAsync(draft.Id,token);
        if(old is not null && old.OwnerUserId!=user)throw new UnauthorizedAccessException();
        Validate(draft);
        var credential=(await credentials.ListAsync(user,token)).FirstOrDefault(c=>c.Id==draft.CredentialId);
        if(credential is null || !credential.HasPassword || credential.Kind!=CredentialKind(draft.Protocol))throw new InvalidOperationException("Choose a saved credential for this protocol before testing the draft.");
        if(draft.HostId is { } host && await hosts.GetAsync(host,token) is null)throw new InvalidOperationException("The associated LMS host no longer exists.");
        return await ProbeAsync(draft.Hostname,draft.Port,draft.Protocol,token);
    }
    public async Task<string> TestCandidateAsync(RemoteDesktopCandidate candidate,CancellationToken token=default)
    {
        var user=await UserAsync(token);
        var approved=Enum.GetValues<LinuxMadeSane.Core.Enums.SshHostDiscoveryScope>()
            .SelectMany(scope=>discoveryJobs?.Snapshot(user,scope).Candidates ?? [])
            .FirstOrDefault(x=>x.Checked && x.Candidate.Hostname==candidate.Hostname && x.Candidate.Port==candidate.Port && x.Candidate.Protocol==candidate.Protocol)?.Candidate;
        if(approved is null)throw new InvalidOperationException("Rescan this candidate before testing it.");
        return await ProbeAsync(approved.Hostname,approved.Port,approved.Protocol,token);
    }
    private static async Task<string> ProbeAsync(string hostname,int port,RemoteDesktopProtocol protocol,CancellationToken token)
    {
        var target=await ResolveTargetAsync(hostname,token);
        var result=await LinuxMadeSane.Infrastructure.Services.SshHostDiscoveryService.ProbeRemoteDesktopAsync(target,port,"Connection test",token,protocol);
        if(result is null)throw new InvalidOperationException("The target is unreachable or its port is closed. Check its address, service and LAN/tailnet route.");
        return result.ProtocolIdentified ? "Protocol identified. The target port responds; Connect verifies its credentials and certificate policy." : "Open port candidate. The port responds but its protocol is not confirmed; no credentials were attempted.";
    }
    public static ConnectionCredentialKind CredentialKind(RemoteDesktopProtocol protocol) => protocol == RemoteDesktopProtocol.Rdp ? ConnectionCredentialKind.Rdp : ConnectionCredentialKind.Vnc;
    public static void Validate(RemoteDesktopConnection c)
    {
        if (c.Id == Guid.Empty || string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 80 || c.Name.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(c.Hostname) || c.Hostname.Length > 255 || c.Hostname.Any(x => char.IsWhiteSpace(x) || char.IsControl(x) || x is '/' or '\\' or ':' or '@' or '?' or '#') && !IPAddress.TryParse(c.Hostname, out _) ||
            !Enum.IsDefined(c.Protocol) || c.Port is < 1 or > 65535 || c.CredentialId == Guid.Empty)
            throw new InvalidOperationException("Check the name, hostname/IP, protocol, port and credential reference.");
        var o = c.Options;
        if (!new[] { "nla", "tls", "rdp", "any" }.Contains(o.SecurityMode) || !new[] { "display-update", "reconnect" }.Contains(o.ResizeMethod) ||
            !new[] { "auto", "zrle", "hextile", "raw" }.Contains(o.VncEncoding) ||
            o.ColourDepth is not (8 or 16 or 24 or 32) || o.Width is < 320 or > 7680 || o.Height is < 240 or > 4320)
            throw new InvalidOperationException("Check the advanced remote desktop options.");
    }
    public static async Task<IPAddress> ResolveTargetAsync(string hostname, CancellationToken token)
    {
        var addresses = IPAddress.TryParse(hostname, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(hostname, token);
        if (addresses.Length == 0 || addresses.Any(a => !Allowed(a)))
            throw new InvalidOperationException("The target must resolve to a unicast LAN, tailnet or explicitly configured remote address. Loopback, link-local and metadata destinations are blocked.");
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
    }
    private static bool Allowed(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.IsIPv6LinkLocal || a.IsIPv6Multicast) return false;
        if (a.AddressFamily == AddressFamily.InterNetwork) { var b = a.GetAddressBytes(); return b[0] != 0 && b[0] < 224 && !(b[0] == 169 && b[1] == 254); }
        return true;
    }
}
