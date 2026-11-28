// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.MailRelay;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Services;
using LinuxMadeSane.Infrastructure.Stores;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Tests.Infrastructure;
public sealed class MailRelayCredentialTests
{
    [Fact]
    public async Task MailServerCredentialsUseTheEncryptedVaultAndCanBeSelectedAgain()
    {
        using var f = new Fixture();
        var saved = await f.Service.SaveAsync(ConnectionCredentialKind.Smtp, "Work mail", "smtp.provider.test", "user", "provider-password");
        var read = await f.Service.ResolveProviderAsync(saved.Id);
        Assert.Equal("provider-password", read.Password);
        Assert.Equal(ConnectionCredentialKind.Smtp, read.Summary.Kind);
        Assert.Equal(587, read.Summary.Port);
        Assert.DoesNotContain(f.Db.ProtectedSecrets, s => s.ProtectedValue.Contains("provider-password"));
        var again = await f.Service.SaveAsync(ConnectionCredentialKind.Smtp, "Work mail", "smtp.provider.test", "user", "provider-password");
        Assert.Equal(saved.Id, again.Id);
        Assert.Single(await f.Service.ListAsync());
    }
    [Fact]
    public async Task DeviceLoginsAreSeparateFromMailProviderLoginsAndStalePasswordsCannotOverwriteRotations()
    {
        using var f = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var configuration = MailRelayConfiguration.CreateDefault(now) with { Enabled = true, RelayHostname = "relay.example.test" };
        await f.Relay.SaveConfigurationAsync(configuration);
        var client = new MailRelayClient(Guid.NewGuid(), configuration.Id, "Office scanner", "office-scanner", MailRelayProvisioningService.HashCredentialPassword("first-device-password"), true, ["example.test"], [], 100, 5000, "", now, now, null);
        await f.Relay.SaveClientAsync(client);
        var saved = await f.Service.SaveAsync(ConnectionCredentialKind.LmsSmtp, "Scanner", "relay.example.test", client.Username, "first-device-password");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ResolveProviderAsync(saved.Id));
        await f.Relay.SaveClientAsync(client with { PasswordHash = MailRelayProvisioningService.HashCredentialPassword("new-device-password") });
        var rotated = await f.Service.SaveAsync(ConnectionCredentialKind.LmsSmtp, "Scanner", "relay.example.test", client.Username, "new-device-password");
        Assert.Equal(saved.Id, rotated.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.SaveAsync(ConnectionCredentialKind.LmsSmtp, "Scanner", "relay.example.test", client.Username, "first-device-password"));
        Assert.Equal("new-device-password", await f.Service.ReadClientPasswordAsync("relay.example.test", client.Username));
        Assert.DoesNotContain(f.Db.ProtectedSecrets, s => s.ProtectedValue.Contains("new-device-password"));
    }
    [Fact]
    public async Task UnauthenticatedSessionsCannotReadOrSaveMailCredentials()
    {
        using var f = new Fixture(); f.Access.User = null;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ListAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveAsync(ConnectionCredentialKind.Smtp, "Mail", "smtp.provider.test", "user", "password"));
        Assert.Empty(f.Db.SavedConnectionCredentials);
        Assert.Empty(f.Db.ProtectedSecrets);
    }
    [Theory]
    [InlineData("Office Scanner", "office-scanner-3")]
    [InlineData("", "smtp-user")]
    [InlineData("PRINTER !!", "printer")]
    public void SuggestedUsernamesAreUsableAndDoNotCollide(string name, string expected) =>
        Assert.Equal(expected, MailRelayClientNames.Suggest(name, ["office-scanner", "OFFICE-SCANNER-2"]));

    private sealed class Access : ISavedCredentialAccessContext
    {
        public Guid? User = Guid.NewGuid();
        public Task<Guid?> GetAuthenticatedUserIdAsync(CancellationToken token = default) => Task.FromResult(User);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public LinuxMadeSaneDbContext Db { get; }
        public Access Access { get; } = new();
        public SqliteMailRelayStore Relay { get; }
        public MailRelayCredentialService Service { get; }
        public Fixture()
        {
            connection.Open();
            Db = new(new DbContextOptionsBuilder<LinuxMadeSaneDbContext>().UseSqlite(connection).Options);
            Db.Database.EnsureCreated();
            var secrets = new SqliteProtectedSecretStore(Db, new EphemeralDataProtectionProvider());
            Relay = new(Db);
            Service = new(Access, new SavedConnectionCredentialService(Db, secrets, Access), Relay);
        }
        public void Dispose() { Db.Dispose(); connection.Dispose(); }
    }
}
