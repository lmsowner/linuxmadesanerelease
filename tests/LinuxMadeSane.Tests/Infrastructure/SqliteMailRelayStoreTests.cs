// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.MailRelay;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Stores;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Tests.Infrastructure;

public sealed class SqliteMailRelayStoreTests
{
    [Fact]
    public async Task Store_RoundTripsConfigurationDomainsClientsAndOwnedDnsRecords()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            await using var dbContext = CreateDbContext(databasePath);
            await dbContext.Database.EnsureCreatedAsync();
            var store = new SqliteMailRelayStore(dbContext);
            var now = DateTimeOffset.UtcNow;
            var configuration = MailRelayConfiguration.CreateDefault(now) with
            {
                Outbound = new() { Mode = MailRelayOutboundMode.Automatic, Host = "smtp.provider.test", Username = "provider-user", PasswordSecretReference = "secret://upstream", SpfInclude = "_spf.provider.test" },
                RelayHostname = "smtp.example.test",
                PublicIpAddress = "203.0.113.10",
                AllowLegacyPort25 = true,
                SubmissionListenAddresses = ["127.0.0.1", "100.64.0.4"],
                LegacyListenAddresses = ["192.168.50.10"],
                LegacyAllowedNetworks = ["192.168.50.40", "192.168.50.64/28"],
                MonitorPublicIpChanges = true,
                PublicIpCheckIntervalMinutes = 90,
                LastPublicIpCheckUtc = now.AddMinutes(-5),
                LastPublicIpChangeUtc = now.AddDays(-1),
                PublicIpMonitorStatus = MailRelayPublicIpMonitorStatus.Healthy,
                PublicIpMonitorDetail = "Public IP and DNS match."
            };
            var domain = new MailRelayDomain(
                Guid.NewGuid(), configuration.Id, "zone", "example.test", true, "lms2026",
                "secret://dkim-current", now, now, "lms2025", "secret://dkim-previous",
                now.AddMonths(-1), now.AddMonths(-1), null, "cf-dkim", "cf-spf", "cf-dmarc",
                MailRelayDnsStatus.Pass, MailRelayDnsStatus.Pass, MailRelayDnsStatus.Pass,
                MailRelayDmarcPolicy.Monitor, null, now, now);
            var client = new MailRelayClient(
                Guid.NewGuid(), configuration.Id, "Flow Production", "flow-prod", "argon2id-hash",
                true, ["example.test"], ["100.64.0.0/10"], 100, 5_000, "Transactional", now, now, null);
            var dnsRecord = new MailRelayDnsRecord(
                Guid.NewGuid(), domain.Id, "cf-record-id", "TXT", "lms2026._domainkey.example.test",
                "DKIM", true, false, null, "v=DKIM1; p=public-key", MailRelayDnsChangeType.Created, now, now);

            await store.SaveConfigurationAsync(configuration);
            await store.SaveDomainAsync(domain);
            await store.SaveClientAsync(client);
            await store.SaveDnsRecordAsync(dnsRecord);

            var savedConfiguration = Assert.IsType<MailRelayConfiguration>(await store.GetConfigurationAsync());
            Assert.Equal(configuration.Outbound, savedConfiguration.Outbound);
            Assert.Equal(configuration.Id, savedConfiguration.Id);
            Assert.Equal(configuration.RelayHostname, savedConfiguration.RelayHostname);
            Assert.Equal(MailRelayDeliveryMode.DirectInternet, savedConfiguration.DeliveryMode);
            Assert.True(savedConfiguration.AllowLegacyPort25);
            Assert.Equal(configuration.SubmissionListenAddresses, savedConfiguration.SubmissionListenAddresses);
            Assert.Equal(configuration.LegacyListenAddresses, savedConfiguration.LegacyListenAddresses);
            Assert.Equal(configuration.LegacyAllowedNetworks, savedConfiguration.LegacyAllowedNetworks);
            Assert.True(savedConfiguration.MonitorPublicIpChanges);
            Assert.Equal(90, savedConfiguration.PublicIpCheckIntervalMinutes);
            Assert.Equal(configuration.LastPublicIpCheckUtc, savedConfiguration.LastPublicIpCheckUtc);
            Assert.Equal(configuration.LastPublicIpChangeUtc, savedConfiguration.LastPublicIpChangeUtc);
            Assert.Equal(MailRelayPublicIpMonitorStatus.Healthy, savedConfiguration.PublicIpMonitorStatus);
            Assert.Equal("Public IP and DNS match.", savedConfiguration.PublicIpMonitorDetail);
            Assert.Equal(domain, Assert.Single(await store.ListDomainsAsync()));
            var savedClient = Assert.Single(await store.ListClientsAsync());
            Assert.Equal(client.Id, savedClient.Id);
            Assert.Equal(client.Name, savedClient.Name);
            Assert.Equal(client.Username, savedClient.Username);
            Assert.Equal(client.PasswordHash, savedClient.PasswordHash);
            Assert.Equal(client.AllowedSenderDomains, savedClient.AllowedSenderDomains);
            Assert.Equal(client.AllowedNetworks, savedClient.AllowedNetworks);
            Assert.Equal(dnsRecord, Assert.Single(await store.ListDnsRecordsAsync(domain.Id)));

            var storedClient = await dbContext.MailRelayClients.AsNoTracking().SingleAsync();
            Assert.Equal("argon2id-hash", storedClient.PasswordHash);
            Assert.DoesNotContain("Password", storedClient.AllowedSenderDomainsJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Initializer_RecreatesMailRelayTablesForAnExistingDatabase()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            await using var dbContext = CreateDbContext(databasePath);
            await dbContext.Database.EnsureCreatedAsync();
            await dbContext.Database.ExecuteSqlRawAsync("DROP TABLE mail_relay_dns_records;");
            await dbContext.Database.ExecuteSqlRawAsync("DROP TABLE mail_relay_clients;");
            await dbContext.Database.ExecuteSqlRawAsync("DROP TABLE mail_relay_domains;");
            await dbContext.Database.ExecuteSqlRawAsync("DROP TABLE mail_relay_configurations;");

            await new SqliteDatabaseInitializer(dbContext).InitializeAsync();

            var tables = await dbContext.Database
                .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name LIKE 'mail_relay_%';")
                .ToArrayAsync();
            Assert.Equal(4, tables.Length);
            Assert.Contains("mail_relay_configurations", tables);
            Assert.Contains("mail_relay_domains", tables);
            Assert.Contains("mail_relay_clients", tables);
            Assert.Contains("mail_relay_dns_records", tables);
        }
        finally
        {
            DeleteTemporaryDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Initializer_AddsZoneDeliveryAndDnsOwnershipFieldsToExistingMailRelayTables()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            await using var dbContext = CreateDbContext(databasePath);
            await dbContext.Database.EnsureCreatedAsync();
            var legacy = MailRelayConfiguration.CreateDefault(DateTimeOffset.UtcNow) with { RelayHostname = "legacy.example.test" };
            await new SqliteMailRelayStore(dbContext).SaveConfigurationAsync(legacy);
            await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE mail_relay_domains DROP COLUMN CloudflareZoneId;");
            await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE mail_relay_configurations DROP COLUMN DeliveryMode;");
            await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE mail_relay_configurations DROP COLUMN OutboundSettingsJson;");
            await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE mail_relay_configurations DROP COLUMN MonitorPublicIpChanges;");
            await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE mail_relay_configurations DROP COLUMN PublicIpCheckIntervalMinutes;");
            await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE mail_relay_configurations DROP COLUMN LastPublicIpCheckUtc;");
            await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE mail_relay_configurations DROP COLUMN LastPublicIpChangeUtc;");
            await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE mail_relay_configurations DROP COLUMN PublicIpMonitorStatus;");
            await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE mail_relay_configurations DROP COLUMN PublicIpMonitorDetail;");
            await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE mail_relay_dns_records DROP COLUMN ModifiedByLms;");
            await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE mail_relay_dns_records DROP COLUMN OriginalValue;");
            await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE mail_relay_dns_records DROP COLUMN CurrentValue;");
            await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE mail_relay_dns_records DROP COLUMN ChangeType;");
            await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE mail_relay_dns_records DROP COLUMN UpdatedUtc;");

            await new SqliteDatabaseInitializer(dbContext).InitializeAsync();

            var domainColumns = await dbContext.Database
                .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('mail_relay_domains');")
                .ToArrayAsync();
            var configurationColumns = await dbContext.Database
                .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('mail_relay_configurations');")
                .ToArrayAsync();
            var dnsColumns = await dbContext.Database
                .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('mail_relay_dns_records');")
                .ToArrayAsync();

            var migrated = await new SqliteMailRelayStore(dbContext).GetConfigurationAsync();
            Assert.Equal(legacy.Id, migrated!.Id);
            Assert.Equal("legacy.example.test", migrated.RelayHostname);
            Assert.Equal(MailRelayOutboundMode.DirectPort25, migrated.Outbound.Mode);
            Assert.Empty(migrated.Outbound.Host);
            Assert.Contains("CloudflareZoneId", domainColumns);
            Assert.Contains("DeliveryMode", configurationColumns);
            Assert.Contains("OutboundSettingsJson", configurationColumns);
            Assert.Contains("MonitorPublicIpChanges", configurationColumns);
            Assert.Contains("PublicIpCheckIntervalMinutes", configurationColumns);
            Assert.Contains("LastPublicIpCheckUtc", configurationColumns);
            Assert.Contains("LastPublicIpChangeUtc", configurationColumns);
            Assert.Contains("PublicIpMonitorStatus", configurationColumns);
            Assert.Contains("PublicIpMonitorDetail", configurationColumns);
            Assert.Contains("ModifiedByLms", dnsColumns);
            Assert.Contains("OriginalValue", dnsColumns);
            Assert.Contains("CurrentValue", dnsColumns);
            Assert.Contains("ChangeType", dnsColumns);
            Assert.Contains("UpdatedUtc", dnsColumns);
        }
        finally
        {
            DeleteTemporaryDatabase(databasePath);
        }
    }

    private static LinuxMadeSaneDbContext CreateDbContext(string databasePath) =>
        new(new DbContextOptionsBuilder<LinuxMadeSaneDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options);

    private static string CreateTemporaryDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "linuxmadesane-mail-relay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "linuxmadesane.db");
    }

    private static void DeleteTemporaryDatabase(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
