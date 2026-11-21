// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Reflection;
using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.MailRelay;
using LinuxMadeSane.Infrastructure.Services;
using LinuxMadeSane.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using LinuxMadeSane.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
namespace LinuxMadeSane.Tests.Web;
public sealed class MailRelaySetupRenderingTests
{
    [Theory]
    [InlineData(MailRelayOutboundMode.DirectPort587)]
    [InlineData(MailRelayOutboundMode.DirectPort25)]
    public async Task SetupSeparatesDeviceAccessAndMailServerCredentialsAndKeepsDnsAdvanced(MailRelayOutboundMode mode)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(new MicrosoftMailRegistration(DispatchProxy.Create<IStreamingLinuxCommandRunner, Data>()));
        services.AddSingleton(new HttpsOutboundMailProvider(DispatchProxy.Create<ISecretStore, Data>(), new FixtureHttp()));
        services.AddSingleton(DispatchProxy.Create<IMailRelayService, Data>());
        services.AddSingleton(DispatchProxy.Create<IMailRelayCredentialService, Data>());
        services.AddSingleton(DispatchProxy.Create<ISecretStore, Data>());
        services.AddSingleton(DispatchProxy.Create<IJSRuntime, Data>());
        services.AddSingleton(new LinuxMadeSaneDbContext(new DbContextOptionsBuilder<LinuxMadeSaneDbContext>().UseSqlite("Data Source=:memory:").Options));
        services.AddSingleton<MailProviderSetupDraftStore>();
        services.AddSingleton<MailProviderConnectionStore>();
        services.AddSingleton<NavigationManager>(new FixtureNavigation());
        services.AddSingleton(DispatchProxy.Create<IOutboundMailProvider, Data>());
        services.AddSingleton(DispatchProxy.Create<IMailDeliveryReceipts, Data>());
        services.AddSingleton(DispatchProxy.Create<IMessagingEmailSettingsStore, Data>());
        services.AddSingleton<GoogleMailAuthorisation>(sp => new(sp.GetRequiredService<IServiceScopeFactory>(), new FixtureHttp()));
        services.AddSingleton<IComponentActivator>(new Activator(mode));
        await using var provider = services.BuildServiceProvider();
        var fixtureDatabase = provider.GetRequiredService<LinuxMadeSaneDbContext>();
        await fixtureDatabase.Database.OpenConnectionAsync(); await fixtureDatabase.Database.EnsureCreatedAsync();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<MailRelayPanel>()).ToHtmlString());
        Assert.Contains("Accept mail from printers, scanners and other devices", html);
        Assert.Contains("Allowed device IPs or networks", html);
        Assert.Contains("without a password", html);
        Assert.Contains("mail-relay-dialog-host", html);
        Assert.DoesNotContain("Mail server username", html);
        Assert.DoesNotContain("Mail server password", html);
        Assert.Contains("Destination SMTP port", html);
        Assert.Contains("No external SMTP account is required", html);
        var advanced = html.IndexOf("<details class=\"mail-relay-setup-advanced\"", StringComparison.Ordinal);
        Assert.True(advanced > 0);
        var mainSetup = html[..advanced];
        Assert.DoesNotContain("DKIM selector", mainSetup);
        Assert.DoesNotContain("Suggested SMTP username", mainSetup);
        Assert.DoesNotContain(" open", html[advanced..html.IndexOf('>', advanced)]);
    }
    [Theory]
    [InlineData(MailRelayOutboundMode.MicrosoftGraph, false, false, false, false)]
    [InlineData(MailRelayOutboundMode.MicrosoftGraph, false, true, false, false)]
    [InlineData(MailRelayOutboundMode.Gmail, false, false, false, false)]
    [InlineData(MailRelayOutboundMode.Gmail, true, false, false, false)]
    [InlineData(MailRelayOutboundMode.AmazonSes, false, false, false, false)]
    [InlineData(MailRelayOutboundMode.MicrosoftGraph, false, false, true, false)]
    [InlineData(MailRelayOutboundMode.MicrosoftGraph, false, false, false, true)]
    public async Task HttpsProviderSetupRendersGuidedAndExistingSettingsWithoutStoredSecrets(MailRelayOutboundMode mode, bool workspace, bool manual, bool waiting, bool preparing)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(new MicrosoftMailRegistration(DispatchProxy.Create<IStreamingLinuxCommandRunner, Data>()));
        services.AddSingleton(new HttpsOutboundMailProvider(DispatchProxy.Create<ISecretStore, Data>(), new FixtureHttp()));
        services.AddSingleton(DispatchProxy.Create<IMailRelayService, Data>());
        services.AddSingleton(DispatchProxy.Create<ISecretStore, Data>());
        services.AddSingleton(DispatchProxy.Create<IJSRuntime, Data>());
        services.AddSingleton(DispatchProxy.Create<IOutboundMailProvider, Data>());
        services.AddSingleton(DispatchProxy.Create<IMailDeliveryReceipts, Data>());
        services.AddSingleton(DispatchProxy.Create<IMessagingEmailSettingsStore, Data>());
        services.AddSingleton<NavigationManager>(new FixtureNavigation());
        services.AddSingleton(new LinuxMadeSaneDbContext(new DbContextOptionsBuilder<LinuxMadeSaneDbContext>().UseSqlite("Data Source=:memory:").Options));
        services.AddSingleton<MailProviderSetupDraftStore>();
        services.AddSingleton<MailProviderConnectionStore>();
        services.AddSingleton<GoogleMailAuthorisation>(sp => new(sp.GetRequiredService<IServiceScopeFactory>(), new FixtureHttp()));
        services.AddSingleton<IComponentActivator>(new Activator(mode, true, workspace, manual: manual, registration: waiting || preparing, preparing: preparing));
        await using var provider = services.BuildServiceProvider();
        var fixtureDatabase = provider.GetRequiredService<LinuxMadeSaneDbContext>();
        await fixtureDatabase.Database.OpenConnectionAsync(); await fixtureDatabase.Database.EnsureCreatedAsync();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<MailRelayProviderSetup>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["SelectedProvider"] = mode }))).ToHtmlString());
        if (waiting || preparing)
        {
            Assert.Contains("Cancel registration", html);
            Assert.DoesNotContain("Sender mailbox", html);
            Assert.DoesNotContain("Guide me through setup", html);
            Assert.DoesNotContain("Send test email", html);
            if (waiting)
            {
                Assert.Contains("ABCD12345", html); Assert.Contains("Copy code", html);
                Assert.Contains("Open Microsoft sign-in", html); Assert.Contains("Waiting for Microsoft sign-in", html);
                Assert.DoesNotContain("Open Microsoft sign-in and enter this code.", html);
            }
            else { Assert.Contains("Preparing required tools", html); Assert.DoesNotContain("Copy code", html); }
        }
        else
        {
        Assert.Contains("Guide me through setup", html); Assert.Contains("I already have settings", html);
        Assert.Contains("Check connection", html); Assert.Contains("Send test email", html); Assert.Contains("Save connection", html); Assert.Contains("Use for live mail", html);
        Assert.Contains("role=\"tablist\"", html); Assert.DoesNotContain("<select class=\"delivery-provider", html);
        Assert.Contains("LMS / Edge Gateway", html); Assert.Contains("Microsoft 365", html); Assert.Contains("Gmail", html); Assert.Contains("Amazon SES", html);
        if (mode == MailRelayOutboundMode.MicrosoftGraph && !manual)
        {
            Assert.Contains("Register with Microsoft 365", html);
            Assert.DoesNotContain("Application (client) ID", html);
            Assert.DoesNotContain("Application secret", html);
            Assert.DoesNotContain("Tenant ID", html);
        }
        Assert.DoesNotContain("must be installed", html);
        Assert.DoesNotContain("AWS CLI", html);
        if (mode == MailRelayOutboundMode.AmazonSes) Assert.Contains("Prepare SES identity", html);
        if (manual) { Assert.Contains("Application certificate (.pfx or .p12)", html); Assert.Contains("Application (client) ID", html); Assert.Contains("Tenant ID", html); }
        if (mode == MailRelayOutboundMode.Gmail && !workspace) { Assert.Contains("Connect Google Account", html); Assert.Contains("seven days", html); Assert.Contains("gmail.send", html); }
        if (workspace) { Assert.Contains("type=\"file\"", html); Assert.Contains("domain-wide delegation", html); }
        }
        var directory = Environment.GetEnvironmentVariable("LMS_MAIL_UI_FIXTURE_DIR");
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            var document = "<!doctype html><html><head><meta charset=utf-8><meta name=viewport content='width=device-width, initial-scale=1'><link rel=stylesheet href=/styles/theme.css><link rel=stylesheet href=/styles/ui.css><link rel=stylesheet href=/app.css><link rel=stylesheet href=/LinuxMadeSane.Web.styles.css></head><body>" + html + "</body></html>";
            await File.WriteAllTextAsync(Path.Combine(directory, mode + (waiting ? "-waiting" : preparing ? "-preparing" : manual ? "-manual" : "") + ".html"), document);
        }
    }
    [Theory]
    [InlineData(MailRelayOutboundMode.DirectPort25)]
    [InlineData(MailRelayOutboundMode.MicrosoftGraph)]
    [InlineData(MailRelayOutboundMode.Gmail)]
    [InlineData(MailRelayOutboundMode.AmazonSes)]
    public async Task ProviderTabsKeepNativeControlsInTheirOwnPanel(MailRelayOutboundMode mode)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(new MicrosoftMailRegistration(DispatchProxy.Create<IStreamingLinuxCommandRunner, Data>()));
        services.AddSingleton(new HttpsOutboundMailProvider(DispatchProxy.Create<ISecretStore, Data>(), new FixtureHttp()));
        var relay = DispatchProxy.Create<IMailRelayService, Data>(); ((Data)(object)relay).Populated = true;
        services.AddSingleton(relay);
        services.AddSingleton(DispatchProxy.Create<IMailRelayCredentialService, Data>());
        services.AddSingleton(DispatchProxy.Create<ISecretStore, Data>());
        services.AddSingleton(DispatchProxy.Create<IJSRuntime, Data>());
        services.AddSingleton(DispatchProxy.Create<IOutboundMailProvider, Data>());
        services.AddSingleton(DispatchProxy.Create<IMailDeliveryReceipts, Data>());
        services.AddSingleton(DispatchProxy.Create<IMessagingEmailSettingsStore, Data>());
        services.AddSingleton<NavigationManager>(new FixtureNavigation());
        await using var db = new LinuxMadeSaneDbContext(new DbContextOptionsBuilder<LinuxMadeSaneDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync(); services.AddSingleton(db);
        services.AddSingleton<MailProviderSetupDraftStore>(); services.AddSingleton<MailProviderConnectionStore>();
        services.AddSingleton<GoogleMailAuthorisation>(sp => new(sp.GetRequiredService<IServiceScopeFactory>(), new FixtureHttp()));
        services.AddSingleton<IComponentActivator>(new Activator(mode, pageOnly: true));
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<MailRelayPanel>()).ToHtmlString());
        Assert.DoesNotContain("<h1", html);
        if (mode == MailRelayOutboundMode.DirectPort25)
        {
            Assert.Contains("Run preflight", html);
            var protection = System.Net.WebUtility.HtmlDecode(html).IndexOf("Public IP protection · Off", StringComparison.Ordinal);
            Assert.True(protection >= 0 && protection < System.Net.WebUtility.HtmlDecode(html).IndexOf(">Configure<", protection, StringComparison.Ordinal));
            Assert.DoesNotContain("Automatically detect and repair public IP changes", html);
            Assert.Contains("smtp-domain-columns", html);
        }
        else Assert.DoesNotContain("Run preflight", html);
        Assert.Contains("tab-trigger active", html);
        Assert.Contains("aria-selected=\"true\"", html);
        Assert.Contains("aria-selected=\"false\"", html);
        Assert.Contains("role=\"tabpanel\"", html);
        if (mode == MailRelayOutboundMode.DirectPort25) { Assert.Contains("SMTP listening interfaces and access", html); Assert.Contains("tailscale0", html); Assert.Contains("100.64.0.4", html); Assert.Contains("Sending domains", html); Assert.Contains("SMTP users", html); Assert.Contains("example.test", html); }
        else { Assert.DoesNotContain("Sending domains", html); Assert.DoesNotContain("SMTP users", html); Assert.Contains("Add connection", html); }
        var directory = Environment.GetEnvironmentVariable("LMS_MAIL_UI_FIXTURE_DIR");
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "Page-" + mode + ".html"), "<!doctype html><html data-theme-scheme='dark'><head><meta charset=utf-8><meta name=viewport content='width=device-width, initial-scale=1'><link rel=stylesheet href=/styles/theme.css><link rel=stylesheet href=/styles/ui.css><link rel=stylesheet href=/app.css><link rel=stylesheet href=/LinuxMadeSane.Web.styles.css></head><body><main style='padding:24px;max-width:1600px;margin:auto'>" + html + "</main></body></html>");
        }
    }
    [Fact]
    public async Task SavingAndTestingExtraConnectionsDoesNotChangeLiveMailOrProvisionAService()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(new MicrosoftMailRegistration(DispatchProxy.Create<IStreamingLinuxCommandRunner, Data>()));
        services.AddSingleton(new HttpsOutboundMailProvider(DispatchProxy.Create<ISecretStore, Data>(), new FixtureHttp()));
        var relay = DispatchProxy.Create<IMailRelayService, Data>(); services.AddSingleton(relay);
        services.AddSingleton(DispatchProxy.Create<ISecretStore, Data>()); services.AddSingleton(DispatchProxy.Create<IJSRuntime, Data>());
        services.AddSingleton(DispatchProxy.Create<IMailDeliveryReceipts, Data>()); services.AddSingleton(DispatchProxy.Create<IMessagingEmailSettingsStore, Data>());
        services.AddSingleton<NavigationManager>(new FixtureNavigation());
        await using var database = new LinuxMadeSaneDbContext(new DbContextOptionsBuilder<LinuxMadeSaneDbContext>().UseSqlite("Data Source=:memory:").Options);
        await database.Database.OpenConnectionAsync(); await database.Database.EnsureCreatedAsync(); services.AddSingleton(database);
        services.AddSingleton<MailProviderSetupDraftStore>(); services.AddSingleton<MailProviderConnectionStore>();
        services.AddSingleton<GoogleMailAuthorisation>(sp => new(sp.GetRequiredService<IServiceScopeFactory>(), new FixtureHttp()));
        var providerMock = new TestProvider(); services.AddSingleton<IOutboundMailProvider>(providerMock);
        var activator = new Activator(MailRelayOutboundMode.MicrosoftGraph, true); services.AddSingleton<IComponentActivator>(activator);
        var liveStore = new LinuxMadeSane.Infrastructure.Stores.SqliteMailRelayStore(database);
        await liveStore.SaveConfigurationAsync(MailRelayConfiguration.CreateDefault(DateTimeOffset.UtcNow) with { Enabled = true });
        var live = await liveStore.GetConfigurationAsync(); var before = System.Text.Json.JsonSerializer.Serialize(live);
        await using var provider = services.BuildServiceProvider(); await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        await renderer.Dispatcher.InvokeAsync(async () => {
            await renderer.RenderComponentAsync<MailRelayProviderSetup>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Configuration"] = live, ["SelectedProvider"] = MailRelayOutboundMode.MicrosoftGraph }));
            var component = activator.ProviderInstance!; var type = component.GetType();
            void Set(string name, object value) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, value);
            Task Run(string name, params object[] args) => (Task)type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, args)!;
            Set("tenant", "d3333333-3333-3333-3333-333333333333"); Set("clientId", "a1111111-1111-1111-1111-111111111111");
            Set("api", new MailRelayApiSettings { CertificateSecretReference = "fixture-encrypted-certificate" });
            Set("connectionName", "Microsoft test A"); Set("sender", "test@example.test"); Set("testRecipient", "recipient@example.test");
            await Run("CheckAsync"); await Run("TestAsync"); await Run("SaveConnectionAsync");
            Set("editingConnectionId", null!); Set("connectionName", "Microsoft test B"); Set("sender", "second@example.test");
            await Run("CheckAsync"); await Run("TestAsync"); await Run("SaveConnectionAsync");
            await Run("ActivateAsync"); // No live-switch confirmation: must not reach provisioning.
            await Run("SelectTabAsync", MailRelayOutboundMode.Gmail); await Run("RefreshAsync");
            Assert.Equal(MailRelayOutboundMode.Gmail, component.SelectedProvider);
        });
        Assert.Equal(2, providerMock.Sends); Assert.Equal(2, (await provider.GetRequiredService<MailProviderConnectionStore>().ListAsync()).Count);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(await liveStore.GetConfigurationAsync()));
        Assert.Empty(((Data)(object)relay).Calls);
    }
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public async Task GuidedRegistrationPersistsAndResumesWithoutManualIdsOrChangingLiveMail(bool legacyDraft, bool interruptedAfterCompletion, bool resolveAlias)
    {
        var services = new ServiceCollection().AddLogging();
        var registrationRunner = new RegistrationRunner { InterruptAfterCompletion = interruptedAfterCompletion, ResolveAlias = resolveAlias }; services.AddSingleton(new MicrosoftMailRegistration(registrationRunner));
        services.AddSingleton(new HttpsOutboundMailProvider(DispatchProxy.Create<ISecretStore, Data>(), new FixtureHttp()));
        var relay = DispatchProxy.Create<IMailRelayService, Data>(); services.AddSingleton(relay);
        services.AddSingleton<ISecretStore>(new RegistrationSecrets()); services.AddSingleton(DispatchProxy.Create<IJSRuntime, Data>());
        services.AddSingleton(DispatchProxy.Create<IMailDeliveryReceipts, Data>()); services.AddSingleton(DispatchProxy.Create<IMessagingEmailSettingsStore, Data>());
        services.AddSingleton<NavigationManager>(new FixtureNavigation());
        await using var database = new LinuxMadeSaneDbContext(new DbContextOptionsBuilder<LinuxMadeSaneDbContext>().UseSqlite("Data Source=:memory:").Options);
        await database.Database.OpenConnectionAsync(); await database.Database.EnsureCreatedAsync(); services.AddSingleton(database);
        services.AddSingleton<MailProviderSetupDraftStore>(); services.AddSingleton<MailProviderConnectionStore>();
        services.AddSingleton<GoogleMailAuthorisation>(sp => new(sp.GetRequiredService<IServiceScopeFactory>(), new FixtureHttp()));
        var providerMock = new TestProvider(); services.AddSingleton<IOutboundMailProvider>(providerMock);
        var activator = new Activator(MailRelayOutboundMode.MicrosoftGraph, true); services.AddSingleton<IComponentActivator>(activator);
        var liveStore = new LinuxMadeSane.Infrastructure.Stores.SqliteMailRelayStore(database);
        await liveStore.SaveConfigurationAsync(MailRelayConfiguration.CreateDefault(DateTimeOffset.UtcNow) with { Enabled = true });
        var live = await liveStore.GetConfigurationAsync(); var before = System.Text.Json.JsonSerializer.Serialize(live);
        await using var provider = services.BuildServiceProvider(); await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        await renderer.Dispatcher.InvokeAsync(async () => {
            await renderer.RenderComponentAsync<MailRelayProviderSetup>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Configuration"] = live, ["SelectedProvider"] = MailRelayOutboundMode.MicrosoftGraph }));
            var component = activator.ProviderInstance!; var type = component.GetType();
            void Set(string name, object value) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, value);
            Task Run(string name, params object[] args) => (Task)type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, args)!;
            Set("sender", "test@example.test");
            await Run("CheckAsync"); Assert.Equal(0, providerMock.Checks);
            await Run("PrepareMicrosoftAsync");
            var draft = await provider.GetRequiredService<MailProviderSetupDraftStore>().GetAsync(MailRelayOutboundMode.MicrosoftGraph);
            Assert.NotNull(draft); Assert.Equal(MicrosoftMailSetupStage.ExchangeAuthenticated, draft.Api.MicrosoftSetupStage);
            Assert.Contains("sign-in succeeded", draft.Api.MicrosoftSetupIssue);
            Assert.Contains("fixture Exchange role assignment denied", draft.Api.MicrosoftSetupDiagnostic);
            Assert.Equal(RegistrationRunner.Tenant, draft.Api.TenantId); Assert.Equal(RegistrationRunner.Client, draft.Api.ClientId);
            Assert.Equal(RegistrationRunner.Principal, draft.Api.MicrosoftServicePrincipalId);
            Assert.Equal("test@example.test", draft.Api.MicrosoftRegistrationMailbox);
            if (legacyDraft)
                await provider.GetRequiredService<MailProviderSetupDraftStore>().SaveAsync(draft with { Api = draft.Api with {
                    TenantId = "", ClientId = "", MicrosoftServicePrincipalId = "", MicrosoftRegistrationMailbox = "", MicrosoftSetupStage = MicrosoftMailSetupStage.None } });
            await Run("OpenAsync");
            Assert.Contains("sign-in succeeded", (string)type.GetField("status", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(component)!);
            Assert.Equal(legacyDraft ? "" : RegistrationRunner.Client, type.GetField("clientId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(component));
            await Run("CheckAsync"); Assert.Equal(0, providerMock.Checks);
            await Run("PrepareMicrosoftAsync");
            await Run("OpenAsync");
            var ready = await provider.GetRequiredService<MailProviderSetupDraftStore>().GetAsync(MailRelayOutboundMode.MicrosoftGraph);
            Assert.Equal(MicrosoftMailSetupStage.MailboxAuthorised, ready!.Api.MicrosoftSetupStage);
            Assert.Empty(ready.Api.MicrosoftSetupDiagnostic);
            Assert.Equal(resolveAlias ? "primary@example.test" : "test@example.test", ready.Api.SenderAddress);
            Assert.Equal(resolveAlias ? RegistrationRunner.MailboxId : "", ready.Api.MicrosoftMailboxObjectId);
            Assert.Contains("$mailbox = 'test@example.test'", registrationRunner.LastScript);
            Assert.Equal(draft.Api.CertificateSecretReference, ready.Api.CertificateSecretReference);
            Assert.Equal(1, ((RegistrationSecrets)provider.GetRequiredService<ISecretStore>()).Stored);
            Assert.Contains("$client = '" + (legacyDraft ? "" : RegistrationRunner.Client) + "'", registrationRunner.LastScript);
            Assert.Contains("$principalId = '" + (legacyDraft ? "" : RegistrationRunner.Principal) + "'", registrationRunner.LastScript);
            await Run("CheckAsync"); Assert.Equal(1, providerMock.Checks);
            Set("sender", "changed@example.test"); await Run("CheckAsync"); Assert.Equal(1, providerMock.Checks);
            var editedSettings = (MailRelayOutboundSettings)type.GetMethod("Settings", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, null)!;
            Assert.Empty(editedSettings.Api.MicrosoftMailboxObjectId);
        });
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(await liveStore.GetConfigurationAsync()));
        Assert.Empty(((Data)(object)relay).Calls);
    }
    private sealed class RegistrationSecrets : ISecretStore
    {
        private readonly Dictionary<string, string> values = [];
        public int Stored;
        public Task<LinuxMadeSane.Core.Models.SecretReferenceMetadata?> GetMetadataAsync(string reference, CancellationToken cancellationToken = default) => Task.FromResult<LinuxMadeSane.Core.Models.SecretReferenceMetadata?>(null);
        public Task<string> StoreSecretAsync(string secret, string purpose, CancellationToken cancellationToken = default) { Stored++; var id = Guid.NewGuid().ToString(); values[id] = secret; return Task.FromResult(id); }
        public Task<string?> ResolveSecretAsync(string reference, CancellationToken cancellationToken = default) => Task.FromResult(values.GetValueOrDefault(reference));
        public Task DeleteSecretAsync(string reference, CancellationToken cancellationToken = default) { values.Remove(reference); return Task.CompletedTask; }
    }
    private sealed class RegistrationRunner : IStreamingLinuxCommandRunner
    {
        public const string Tenant = "d3333333-3333-3333-3333-333333333333", Client = "a1111111-1111-1111-1111-111111111111", Principal = "e1111111-1111-1111-1111-111111111111";
        public string LastScript = ""; private int attempts;
        public bool InterruptAfterCompletion, ResolveAlias;
        public const string MailboxId = "f1111111-1111-1111-1111-111111111111";
        public Task<LinuxMadeSane.Core.Models.RdpOptimizer.LinuxCommandResult> RunStreamingAsync(LinuxMadeSane.Core.Models.RdpOptimizer.LinuxCommandRequest request, bool dryRun, Action<LinuxMadeSane.Core.Models.RdpOptimizer.LinuxCommandOutput> output, CancellationToken token = default)
        {
            if (request.Description == "Check Microsoft registration runtime") return Task.FromResult(new LinuxMadeSane.Core.Models.RdpOptimizer.LinuxCommandResult("fixture", 0, "", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false));
            LastScript = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(request.Arguments[^1]));
            var directory = Environment.GetEnvironmentVariable("LMS_MAIL_UI_FIXTURE_DIR");
            if (directory is not null && LastScript.Contains("$client = '" + Client + "'")) File.WriteAllText(Path.Combine(directory, "Microsoft-resume.ps1"), LastScript);
            var kind = ++attempts == 1 ? "registered" : "complete";
            var payload = new Dictionary<string, object> { ["kind"] = kind, ["tenantId"] = Tenant, ["clientId"] = Client, ["principalId"] = Principal };
            if (ResolveAlias && kind == "complete") { payload["senderAddress"] = "primary@example.test"; payload["mailboxId"] = MailboxId; }
            output(new("LMS_MAIL_SETUP:" + System.Text.Json.JsonSerializer.Serialize(payload), false, DateTimeOffset.UtcNow));
            if (attempts == 1) {
                output(new("LMS_MAIL_SETUP:" + System.Text.Json.JsonSerializer.Serialize(new { kind = "exchange-connected", tenantId = Tenant, clientId = Client, principalId = Principal }), false, DateTimeOffset.UtcNow));
                if (ResolveAlias) output(new("LMS_MAIL_SETUP:" + System.Text.Json.JsonSerializer.Serialize(new { kind = "mailbox-resolved", tenantId = Tenant, clientId = Client, principalId = Principal, senderAddress = "primary@example.test", mailboxId = MailboxId }), false, DateTimeOffset.UtcNow));
                output(new("LMS_MAIL_SETUP:" + System.Text.Json.JsonSerializer.Serialize(new { kind = "error", stage = "role", command = "New-ManagementRoleAssignment", detail = "fixture Exchange role assignment denied" }), false, DateTimeOffset.UtcNow));
            }
            if (attempts > 1 && InterruptAfterCompletion) return Task.FromException<LinuxMadeSane.Core.Models.RdpOptimizer.LinuxCommandResult>(new OperationCanceledException());
            return Task.FromResult(new LinuxMadeSane.Core.Models.RdpOptimizer.LinuxCommandResult("fixture", attempts == 1 ? 1 : 0, "", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false));
        }
    }
    private sealed class TestProvider : IOutboundMailProvider
    {
        public int Sends, Checks;
        public Task<MailDeliveryResult> CheckAsync(MailRelayOutboundSettings settings, CancellationToken token = default) { Checks++; return Task.FromResult(new MailDeliveryResult(MailDeliveryOutcome.Accepted, "Fixture sign-in verified")); }
        public Task<MailDeliveryResult> SendAsync(MailRelayOutboundSettings settings, OutboundMailMessage message, CancellationToken token = default) { Sends++; return Task.FromResult(new MailDeliveryResult(MailDeliveryOutcome.Accepted, "Fixture accepted")); }
    }
    [Fact]
    public void ExistingDeviceInterfacesArePreservedUnlessTheUserChangesThem()
    {
        var component = new MailRelayPanel();
        var type = typeof(MailRelayPanel);
        void Set(string name, object value) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, value);
        var configuration = MailRelayConfiguration.CreateDefault(DateTimeOffset.UtcNow) with { Enabled = true, AllowLegacyPort25 = true, LegacyListenAddresses = ["10.0.0.4", "100.64.0.4"], LegacyAllowedNetworks = ["10.0.0.40"] };
        Set("dashboard", new MailRelayDashboardViewModel(MailRelayOperationalStatus.Healthy, "", configuration, [], [], Infrastructure.MailRelayOutboundTests.Preflight(true)));
        Set("setupDevicesEnabled", true); Set("setupDeviceAddress", "10.0.0.4"); Set("setupDeviceSources", "10.0.0.40");
        MailRelayLegacySubmissionRequest Request() => (MailRelayLegacySubmissionRequest)type.GetMethod("BuildDeviceSubmissionRequest", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, null)!;
        Assert.Equal(configuration.LegacyListenAddresses, Request().ListenAddresses);
        Set("setupDeviceAddress", "10.0.0.5"); Assert.Equal(new[] { "10.0.0.5" }, Request().ListenAddresses);
        Set("setupDevicesEnabled", false); Assert.False(Request().Enabled); Assert.Empty(Request().ListenAddresses);
    }

    private sealed class FixtureNavigation : NavigationManager { public FixtureNavigation() => Initialize("https://lms.example.test/", "https://lms.example.test/services"); protected override void NavigateToCore(string uri, bool forceLoad) => throw new InvalidOperationException(); }
    private sealed class FixtureHttp : IHttpClientFactory { public HttpClient CreateClient(string name) => throw new InvalidOperationException("No network in rendering tests"); }
    private sealed class Activator(MailRelayOutboundMode mode, bool providerOpen = false, bool workspace = false, bool pageOnly = false, bool manual = false, bool registration = false, bool preparing = false) : IComponentActivator
    {
        public MailRelayProviderSetup? ProviderInstance { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)System.Activator.CreateInstance(type)!;
            if (component is MailRelayPanel)
            {
                type.GetField("domainEditorOpen", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, !pageOnly);
                type.GetField("deliveryTab", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, pageOnly ? mode : MailRelayOutboundMode.DirectPort25);
                type.GetField("outboundMode", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, mode);
            }
            if (component is MailRelayProviderSetup instance) ProviderInstance = instance;
            if (providerOpen && component is MailRelayProviderSetup)
            {
                type.GetField("open", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, true);
                type.GetField("mode", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, mode);
                type.GetField("workspace", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, workspace);
                type.GetField("guided", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, !manual);
                if (registration)
                {
                    type.GetField("registrationCancellation", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, new CancellationTokenSource());
                    type.GetField("busy", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, true);
                    type.GetField("sender", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, "sender@example.test");
                    type.GetField("microsoftDeviceCode", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, preparing ? "" : "ABCD12345");
                    type.GetField("status", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, preparing ? "Preparing required tools…" : "Open Microsoft sign-in and enter this code.");
                }
            }
            return component;
        }
    }
    public class Data : DispatchProxy
    {
        public List<string> Calls { get; } = [];
        public bool Populated { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls.Add(method!.Name);
            if (method!.Name == "ListAttentionAsync") return Task.FromResult<IReadOnlyList<MailDeliveryReceipt>>([]);
            if (method!.Name == "ListAsync") return Task.FromResult<IReadOnlyList<SavedConnectionCredentialSummary>>([]);
            if (method.Name == "GetLatestProvisioningJob") return null;
            if (method.Name != "GetDashboardAsync") throw new InvalidOperationException("No state changes in this fixture");
            var preflight = Infrastructure.MailRelayOutboundTests.Preflight(true) with { AvailableZones = [new("zone", "example.test", "active", false, true)] };
            if (!Populated) return Task.FromResult(new MailRelayDashboardViewModel(MailRelayOperationalStatus.NotConfigured, "fixture", null, [], [], preflight));
            var now = DateTimeOffset.UtcNow;
            var config = MailRelayConfiguration.CreateDefault(now) with { Enabled = true, RelayHostname = "smtp.example.test", PublicIpAddress = "203.0.113.10" };
            T Model<T>(object data) => System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(data))!;
            var domain = Model<MailRelayDomain>(new { Id = Guid.NewGuid(), MailRelayConfigurationId = config.Id, CloudflareZoneId = "zone", DomainName = "example.test", Enabled = true, CurrentDkimSelector = "lms", SpfStatus = MailRelayDnsStatus.Pass, DkimStatus = MailRelayDnsStatus.Pass, DmarcStatus = MailRelayDnsStatus.Pass, CreatedUtc = now, UpdatedUtc = now });
            var client = new MailRelayClient(Guid.NewGuid(), config.Id, "LMS notifications", "lms-notifications", "fixture", true, ["example.test"], [], 60, 1000, "", now, now, now);
            return Task.FromResult(new MailRelayDashboardViewModel(MailRelayOperationalStatus.NotConfigured, "fixture", config, [domain], [client], preflight) { Listeners = [new("lo", "127.0.0.1", 587, true, "SMTP user required", "STARTTLS required"), new("tailscale0", "100.64.0.4", 587, true, "SMTP user required", "STARTTLS required")] });
        }
    }
}
