// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Data.Common;
using System.Reflection;
using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Application.Services;
using LinuxMadeSane.Core.Models.MailRelay;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Stores;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LinuxMadeSane.Tests.Application;

public sealed class MailRelayDashboardLoadingTests
{
    [Fact]
    public async Task ColdDashboardDoesNotOverlapReadsOnTheSharedDatabaseContext()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var delay = new DelayedReads();
        await using var db = new LinuxMadeSaneDbContext(new DbContextOptionsBuilder<LinuxMadeSaneDbContext>()
            .UseSqlite(connection).AddInterceptors(delay).Options);
        await db.Database.EnsureCreatedAsync();
        var store = new SqliteMailRelayStore(db);
        var configuration = MailRelayConfiguration.CreateDefault(DateTimeOffset.UtcNow);
        await store.SaveConfigurationAsync(configuration);
        var preflight = DispatchProxy.Create<IMailRelayPreflightService, PreflightProxy>();
        ((PreflightProxy)(object)preflight).Inspect = async () =>
        {
            Assert.NotNull(await store.GetConfigurationAsync());
            return new MailRelayPreflightResult("", "", [], "", "", "", [], false, DateTimeOffset.UtcNow);
        };
        delay.Enabled = true;
        var service = new MailRelayService(store, preflight, null!, null!, null!, null!, null!);
        var result = await service.GetDashboardAsync();
        Assert.Equal(configuration.Id, result.Configuration!.Id);
        Assert.Empty(result.Domains);
        Assert.Empty(result.Clients);
    }

    private sealed class DelayedReads : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled) await Task.Delay(30, cancellationToken);
            return result;
        }
    }
    public class PreflightProxy : DispatchProxy
    {
        public Func<Task<MailRelayPreflightResult>> Inspect { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Inspect();
    }
}
