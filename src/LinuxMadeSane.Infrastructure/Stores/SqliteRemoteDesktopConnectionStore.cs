// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RemoteDesktop;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
namespace LinuxMadeSane.Infrastructure.Stores;
public sealed class SqliteRemoteDesktopConnectionStore(LinuxMadeSaneDbContext db) : IRemoteDesktopConnectionStore
{
    public async Task<IReadOnlyList<RemoteDesktopConnection>> ListAsync(Guid owner, CancellationToken token = default) =>
        (await db.RemoteDesktopConnections.AsNoTracking().Where(c => c.OwnerUserId == owner).OrderBy(c => c.Name).ToArrayAsync(token)).Select(Map).ToArray();
    public async Task<RemoteDesktopConnection?> GetAsync(Guid id, CancellationToken token = default) =>
        await db.RemoteDesktopConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, token) is { } item ? Map(item) : null;
    public async Task SaveAsync(RemoteDesktopConnection connection, CancellationToken token = default)
    {
        var item = await db.RemoteDesktopConnections.SingleOrDefaultAsync(c => c.Id == connection.Id, token);
        if (item is null) { item = new() { Id = connection.Id }; db.RemoteDesktopConnections.Add(item); }
        item.OwnerUserId = connection.OwnerUserId; item.HostId = connection.HostId; item.Name = connection.Name;
        item.Hostname = connection.Hostname; item.Protocol = (int)connection.Protocol; item.Port = connection.Port;
        item.CredentialId = connection.CredentialId; item.OptionsJson = JsonSerializer.Serialize(connection.Options);
        item.Revision = connection.Revision; item.UpdatedAtUtc = connection.UpdatedAtUtc;
        await db.SaveChangesAsync(token);
    }
    public async Task DeleteAsync(Guid id, CancellationToken token = default)
    {
        var item = await db.RemoteDesktopConnections.FindAsync([id], token);
        if (item is not null) { db.RemoteDesktopConnections.Remove(item); await db.SaveChangesAsync(token); }
    }
    private static RemoteDesktopConnection Map(RemoteDesktopConnectionEntity c) => new(c.Id, c.OwnerUserId, c.HostId,
        c.Name, c.Hostname, (RemoteDesktopProtocol)c.Protocol, c.Port, c.CredentialId,
        JsonSerializer.Deserialize<RemoteDesktopOptions>(c.OptionsJson) ?? new(), c.Revision, c.UpdatedAtUtc);
}
