// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace LinuxMadeSane.Infrastructure.Licensing;
public static class LicensingSchema
{
    public static Task EnsureAsync(LinuxMadeSaneDbContext db, CancellationToken token = default) => db.Database.ExecuteSqlRawAsync(
        "CREATE TABLE IF NOT EXISTS licensing_records (Id INTEGER NOT NULL PRIMARY KEY, Revision INTEGER NOT NULL, Json TEXT NOT NULL);", token);
}
