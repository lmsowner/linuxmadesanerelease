// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Text.Json;
using LinuxMadeSane.Core.Models.MailRelay;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class MailProviderConnectionStore(LinuxMadeSaneDbContext database)
{
    private const string Prefix = "mail-relay-connection:";
    public async Task<IReadOnlyList<MailProviderConnection>> ListAsync(CancellationToken token = default)
    {
        var rows = await database.InfrastructureStates.AsNoTracking().Where(r => r.Key.StartsWith(Prefix)).ToListAsync(token);
        return rows.Select(r => JsonSerializer.Deserialize<MailProviderConnection>(r.Json)!).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public async Task SaveAsync(MailProviderConnection connection, CancellationToken token = default)
    {
        if (connection.Id == Guid.Empty || string.IsNullOrWhiteSpace(connection.Name) || connection.Name.Length > 80)
            throw new InvalidOperationException("Give this connection a name up to 80 characters.");
        var key = Prefix + connection.Id.ToString("N");
        var row = await database.InfrastructureStates.FindAsync([key], token);
        if (row is null) { row = new InfrastructureStateEntity { Key = key }; database.InfrastructureStates.Add(row); }
        row.Json = JsonSerializer.Serialize(connection with { Name = connection.Name.Trim(), Settings = Normalize(connection.Settings) });
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(token);
    }
    public async Task PreserveLiveAsync(MailRelayConfiguration? configuration, CancellationToken token = default)
    {
        if (configuration?.Enabled != true || (await ListAsync(token)).Any(c => Matches(c.Settings, configuration.Outbound))) return;
        await SaveAsync(new(Guid.NewGuid(), configuration.Outbound.UsesHttpsApi ? "Previous live " + configuration.Outbound.Mode : "Existing LMS / Edge Gateway",
            configuration.Outbound, DateTimeOffset.UtcNow), token);
    }
    public async Task ClearAsync(CancellationToken token = default)
    {
        await database.InfrastructureStates.Where(r => r.Key.StartsWith(Prefix)).ExecuteDeleteAsync(token);
        foreach (var entry in database.ChangeTracker.Entries<InfrastructureStateEntity>().Where(e => e.Entity.Key.StartsWith(Prefix)).ToArray()) entry.State = EntityState.Detached;
    }
    public static bool Matches(MailRelayOutboundSettings left, MailRelayOutboundSettings right) => Normalize(left) == Normalize(right);
    private static MailRelayOutboundSettings Normalize(MailRelayOutboundSettings settings) => settings with { Api = settings.Api with { BridgeSecretReference = null } };
}
