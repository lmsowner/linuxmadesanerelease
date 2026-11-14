// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Text.Json;
using LinuxMadeSane.Core.Licensing;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
namespace LinuxMadeSane.Infrastructure.Licensing;

// Additive schema; ordinary database backup/restore preserves declarations, original trial and audit.
public sealed class SqliteLicensingStore(DbContextOptions<LinuxMadeSaneDbContext> options)
{
    public async Task<(LicensingState State, long Revision)> ReadAsync(CancellationToken token = default)
    {
        await using var db = new LinuxMadeSaneDbContext(options);
        var row = await db.Set<LicensingRecordEntity>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == 1, token);
        return (row is null ? new() : JsonSerializer.Deserialize<LicensingState>(row.Json) ?? throw new InvalidDataException("Invalid stored licensing record."), row?.Revision ?? 0);
    }
    public async Task<long> SaveAsync(LicensingState state, long expectedRevision, CancellationToken token = default)
    {
        await using var db = new LinuxMadeSaneDbContext(options);
        if (state.SchemaVersion != 1) throw new ArgumentException("Unsupported schema.");
        var row = new LicensingRecordEntity { Id = 1, Revision = expectedRevision + 1, Json = JsonSerializer.Serialize(state) };
        if (expectedRevision == 0) db.Add(row);
        else
        {
            db.Attach(row);
            db.Entry(row).Property(r => r.Revision).OriginalValue = expectedRevision;
            db.Entry(row).Property(r => r.Revision).IsModified = true;
            db.Entry(row).Property(r => r.Json).IsModified = true;
        }
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException) { db.Entry(row).State = EntityState.Detached; throw new InvalidOperationException("Licensing state changed. Refresh and retry."); }
        db.Entry(row).State = EntityState.Detached;
        return row.Revision;
    }
}
