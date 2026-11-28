// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.MailRelay;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Infrastructure.Services;
using LinuxMadeSane.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LinuxMadeSane.Tests.Infrastructure;

public sealed class MailRelayMicrosoftRegistrationTests
{
    [Fact]
    public void ProductionRegistrationsResolveMicrosoftSetupUsingTheSharedCommandRunner()
    {
        var root = Path.Combine(Path.GetTempPath(), "lms-mail-composition-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["ConnectionStrings:LinuxMadeSane"] = "Data Source=" + Path.Combine(root, "fixture.db")
            }).Build();
            var services = new ServiceCollection().AddLogging();
            services.AddInfrastructureServices(configuration, root);
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            using var scope = provider.CreateScope();
            Assert.Same(scope.ServiceProvider.GetRequiredService<ILinuxCommandRunner>(), scope.ServiceProvider.GetRequiredService<IStreamingLinuxCommandRunner>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<MicrosoftMailRegistration>());
            Assert.Same(scope.ServiceProvider.GetRequiredService<LinuxMadeSane.Application.Contracts.MailRelay.IOutboundMailProvider>(), scope.ServiceProvider.GetRequiredService<HttpsOutboundMailProvider>());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private const string Tenant = "d3333333-3333-3333-3333-333333333333", Client = "a1111111-1111-1111-1111-111111111111";
    private static string Complete(string tenant = Tenant) => "LMS_MAIL_SETUP:{\"kind\":\"complete\",\"tenantId\":\"" + tenant + "\",\"clientId\":\"" + Client + "\"}";
    [Fact]
    public async Task CapturesIdsAndBothSignInsWithoutForwardingRawSdkOutput()
    {
        var runner = new Runner(0, ["LMS_MAIL_SETUP:{\"kind\":\"progress\",\"stage\":\"graph\"}",
            "To sign in, open https://microsoft.com/devicelogin", "and enter the code ABCD12345 to authenticate.",
            "sensitive raw SDK detail must never reach browser", "LMS_MAIL_SETUP:{\"kind\":\"progress\",\"stage\":\"exchange\"}",
            "To sign in, open https://microsoft.com/devicelogin and enter the code EFGH67890 to authenticate.", Complete()]);
        var progress = new List<MicrosoftMailRegistrationProgress>();
        var result = await new MicrosoftMailRegistration(runner).RegisterAsync("fixture script", progress.Add);
        Assert.Equal(new(Tenant, Client), result);
        Assert.Equal(new[] { "ABCD12345", "EFGH67890" }, progress.Where(p => p.DeviceCode is not null).Select(p => p.DeviceCode));
        Assert.DoesNotContain(progress, p => p.Message.Contains("sensitive"));
        Assert.False(runner.Request!.RequiresSudo);
        Assert.Equal("pwsh", runner.Request.FileName);
        Assert.Contains("-NonInteractive", runner.Request.Arguments);
        Assert.Equal("fixture script", Encoding.Unicode.GetString(Convert.FromBase64String(runner.Request.Arguments[^1])));
    }
    [Theory]
    [InlineData(1, "did not finish")]
    [InlineData(124, "timed out")]
    [InlineData(127, "prepare its tools again")]
    [InlineData(78, "prepare its tools again")]
    public async Task FailureCannotCommitAnIdentityEvenIfCompletionWasPrinted(int exit, string message)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new MicrosoftMailRegistration(new Runner(exit, [Complete()])).RegisterAsync("script", _ => { }));
        Assert.Contains(message, exception.Message);
        Assert.DoesNotContain("raw secret", exception.Message);
    }
    [Theory]
    [InlineData("tenant", "different tenants")]
    [InlineData("sender", "primary email address")]
    [InlineData("scope", "permission propagation")]
    [InlineData("mailbox", "sign-in succeeded")]
    [InlineData("principal", "registered in Exchange")]
    [InlineData("scope-create", "sending scope")]
    [InlineData("scope-conflict", "different or broader")]
    [InlineData("role", "could not be assigned")]
    [InlineData("verify", "verification did not finish")]
    public async Task KnownRegistrationProblemsHaveSpecificRemediation(string stage, string expected)
    {
        var error = "LMS_MAIL_SETUP:{\"kind\":\"error\",\"stage\":\"" + stage + "\"}";
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new MicrosoftMailRegistration(new Runner(0, [error, Complete()])).RegisterAsync("script", _ => { }));
        Assert.Contains(expected, exception.Message);
    }
    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidIdentityCannotCompleteRegistration(string tenant)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MicrosoftMailRegistration(new Runner(0, ["LMS_MAIL_SETUP:{broken", Complete(tenant)])).RegisterAsync("script", _ => { }));
    }
    [Fact]
    public async Task CancellationIsPassedToExistingRunner()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MicrosoftMailRegistration(new Runner(0, [])).RegisterAsync("script", _ => { }, cancellation.Token));
    }
    [Fact]
    public void GeneratedScriptDiscoversTenantUsesPublicCertificateAndScopesExchangeToSender()
    {
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=Fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var script = MailProviderSetupGuidance.Microsoft("", "sender@example.test", Convert.ToBase64String(certificate.RawData));
        Assert.Contains("Get-MgContext).TenantId", script);
        Assert.Contains("Get-ConnectionInformation", script);
        Assert.Contains("Connect-ExchangeOnline -Device", script);
        Assert.Contains("-Scope CurrentUser", script);
        Assert.Contains("Prefer='create-if-missing'", script);
        Assert.Contains("-Role 'Application Mail.Send'", script);
        Assert.Contains("-CustomResourceScope $scopeName", script);
        Assert.Contains("Test-ServicePrincipalAuthorization", script);
        Assert.Contains("kind='complete'", script);
        Assert.DoesNotContain("Primary sender required", script);
        Assert.Contains("-Properties PrimarySmtpAddress,ExternalDirectoryObjectId", script);
        Assert.Contains("kind='mailbox-resolved'", script);
        Assert.Contains("Find-MailboxOnlyScope", script);
        Assert.Contains("Test-MailboxOnlyScope", script);
        Assert.Contains("scope-reuse", script);
        Assert.DoesNotContain("client_secret", script);
        Assert.DoesNotContain(Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12)), script);
        Assert.NotEqual(script, MailProviderSetupGuidance.Microsoft("", "other@example.test", Convert.ToBase64String(certificate.RawData)));
        var directory = Environment.GetEnvironmentVariable("LMS_MAIL_UI_FIXTURE_DIR");
        if (directory is not null) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "Microsoft-registration.ps1"), script); }
    }
    [Theory]
    [InlineData(1)]
    [InlineData(124)]
    public async Task ApplicationCheckpointSurvivesLaterExchangeFailure(int exit)
    {
        var checkpoint = Complete().Replace("complete", "registered");
        var saved = new List<MicrosoftMailRegistrationResult>();
        var runner = new Runner(exit, [checkpoint, "LMS_MAIL_SETUP:{\"kind\":\"progress\",\"stage\":\"exchange\"}"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MicrosoftMailRegistration(runner).RegisterAsync("script", _ => { },
            (identity, _) => { saved.Add(identity); return Task.CompletedTask; }));
        Assert.Equal(new(Tenant, Client) { Stage = MicrosoftMailSetupStage.ApplicationRegistered }, saved[0]);
        Assert.Equal(MicrosoftMailSetupStage.ApplicationRegistered, saved[^1].Stage);
        Assert.Contains("did not finish", saved[^1].Issue);
    }
    [Fact]
    public async Task CheckpointIsSavedWhileExchangeIsStillWaitingAndSurvivesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var saved = new TaskCompletionSource<MicrosoftMailRegistrationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new WaitingRunner();
        var registration = new MicrosoftMailRegistration(runner).RegisterAsync("script", _ => { },
            (identity, _) => { saved.TrySetResult(identity); return Task.CompletedTask; }, cancellation.Token);
        Assert.Equal(new(Tenant, Client) { Stage = MicrosoftMailSetupStage.ApplicationRegistered }, await saved.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(registration.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registration);
    }
    [Fact]
    public async Task FailedCheckpointStorageStopsRegistration()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new MicrosoftMailRegistration(new WaitingRunner())
            .RegisterAsync("script", _ => { }, (_, _) => Task.FromException(new IOException("private storage detail"))));
        Assert.Contains("could not be saved", exception.Message);
        Assert.DoesNotContain("private storage detail", exception.Message);
    }
    [Fact]
    public async Task ResolvedMailboxIdentityIsRetainedForSendingAndFinalCompletion()
    {
        const string mailboxId = "f1111111-1111-1111-1111-111111111111";
        var resolved = "LMS_MAIL_SETUP:" + System.Text.Json.JsonSerializer.Serialize(new { kind = "mailbox-resolved", tenantId = Tenant, clientId = Client, senderAddress = "primary@example.test", mailboxId });
        var saved = new List<MicrosoftMailRegistrationResult>();
        var result = await new MicrosoftMailRegistration(new Runner(0, [resolved, resolved.Replace("mailbox-resolved", "complete")]))
            .RegisterAsync("script", _ => { }, (value, _) => { saved.Add(value); return Task.CompletedTask; });
        Assert.Equal("primary@example.test", result.SenderAddress); Assert.Equal(mailboxId, result.MailboxObjectId);
        Assert.Equal(MicrosoftMailSetupStage.ExchangeAuthenticated, saved[0].Stage);
        Assert.Equal(MicrosoftMailSetupStage.MailboxAuthorised, saved[^1].Stage);
    }
    [Theory]
    [InlineData("scope-read", "Get-ManagementScope", "could not read")]
    [InlineData("scope-create", "New-ManagementScope", "could not create")]
    [InlineData("exchange-preflight", "New-ManagementScope", "Role Management")]
    public async Task ExchangeResponseIsRetainedWithItsOperationAndCredentialsRedacted(string stage, string command, string expected)
    {
        var connected = Complete().Replace("complete", "exchange-connected");
        var detail = "Access denied by Exchange. Authorization: Basic secret-basic Bearer secret-bearer access_token=secret-access client_secret='secret-client' password=secret-password https://secret-user:secret-url-password@example.test/api?code=secret-query#secret-fragment ";
        var error = "LMS_MAIL_SETUP:" + System.Text.Json.JsonSerializer.Serialize(new { kind = "error", stage, command, detail });
        var saved = new List<MicrosoftMailRegistrationResult>();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new MicrosoftMailRegistration(new Runner(1, [connected, error]))
            .RegisterAsync("script", _ => { }, (value, _) => { saved.Add(value); return Task.CompletedTask; }));
        Assert.Contains(expected, exception.Message);
        var diagnostic = saved[^1].Diagnostic;
        Assert.Contains(command, diagnostic); Assert.Contains("Access denied by Exchange", diagnostic);
        Assert.DoesNotContain("secret-", diagnostic); Assert.Contains("https://example.test/api", diagnostic);
        Assert.Equal(MicrosoftMailSetupStage.ExchangeAuthenticated, saved[^1].Stage);
    }
    [Fact]
    public async Task TruncatedKeyAndLongTokenAreNeverSavedInExchangeDiagnostic()
    {
        var detail = "Rejected " + new string('q', 160) + " eyJheader.payload.signature -----BEGIN PRIVATE KEY-----\nprivate material without closing marker";
        var error = "LMS_MAIL_SETUP:" + System.Text.Json.JsonSerializer.Serialize(new { kind = "error", stage = "scope-create", command = "New-ManagementScope", detail });
        var saved = new List<MicrosoftMailRegistrationResult>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MicrosoftMailRegistration(new Runner(1, [Complete().Replace("complete", "exchange-connected"), error]))
            .RegisterAsync("script", _ => { }, (value, _) => { saved.Add(value); return Task.CompletedTask; }));
        Assert.DoesNotContain(new string('q', 160), saved[^1].Diagnostic); Assert.DoesNotContain("eyJheader", saved[^1].Diagnostic);
        Assert.DoesNotContain("private material", saved[^1].Diagnostic); Assert.Contains("[redacted key]", saved[^1].Diagnostic);
    }
    private sealed class WaitingRunner : IStreamingLinuxCommandRunner
    {
        public async Task<LinuxCommandResult> RunStreamingAsync(LinuxCommandRequest request, bool dryRun, Action<LinuxCommandOutput> output, CancellationToken token = default)
        {
            if (request.Description == "Check Microsoft registration runtime") return new("fixture", 0, "", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);
            output(new(Complete().Replace("complete", "registered"), false, DateTimeOffset.UtcNow));
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Fixture unexpectedly resumed");
        }
    }
    private sealed class Runner(int exit, string[] lines) : IStreamingLinuxCommandRunner
    {
        public LinuxCommandRequest? Request;
        public Task<LinuxCommandResult> RunStreamingAsync(LinuxCommandRequest request, bool dryRun, Action<LinuxCommandOutput> output, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (request.Description == "Check Microsoft registration runtime") return Task.FromResult(new LinuxCommandResult("fixture", 0, "", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false));
            Request = request;
            foreach (var line in lines) output(new(line, false, DateTimeOffset.UtcNow));
            return Task.FromResult(new LinuxCommandResult("fixture", exit, "raw secret", "raw secret", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false));
        }
    }
}
