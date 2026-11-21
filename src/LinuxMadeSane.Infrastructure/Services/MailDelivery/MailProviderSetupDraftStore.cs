// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Text.Json;
using LinuxMadeSane.Core.Models.MailRelay;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Services;

// Reuse existing configuration persistence. Drafts contain references, never credentials.
public sealed class MailProviderSetupDraftStore(LinuxMadeSaneDbContext database)
{
    private static string Key(MailRelayOutboundMode mode) => "mail-relay-provider-draft:" + (int)mode;
    public async Task<MailRelayOutboundSettings?> GetAsync(MailRelayOutboundMode mode, CancellationToken token = default)
    {
        var row = await database.InfrastructureStates.AsNoTracking().SingleOrDefaultAsync(r => r.Key == Key(mode), token);
        return row is null ? null : JsonSerializer.Deserialize<MailRelayOutboundSettings>(row.Json);
    }
    public async Task SaveAsync(MailRelayOutboundSettings settings, CancellationToken token = default)
    {
        var key = Key(settings.Mode); var row = await database.InfrastructureStates.FindAsync([key], token);
        if (row is null) { row = new InfrastructureStateEntity { Key = key }; database.InfrastructureStates.Add(row); }
        row.Json = JsonSerializer.Serialize(settings); row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(token);
    }
    public async Task ClearAsync(MailRelayOutboundMode mode, CancellationToken token = default)
    {
        var key = Key(mode);
        await database.InfrastructureStates.Where(r => r.Key == key).ExecuteDeleteAsync(token);
        foreach (var entry in database.ChangeTracker.Entries<InfrastructureStateEntity>().Where(e => e.Entity.Key == key).ToArray()) entry.State = EntityState.Detached;
    }
}
