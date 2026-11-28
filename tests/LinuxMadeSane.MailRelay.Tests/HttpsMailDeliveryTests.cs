// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Formats.Tar;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LinuxMadeSane.Core.Models.Messaging;
using System.Text;
using System.Text.Json;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.MailRelay;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Services;
using LinuxMadeSane.Infrastructure.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinuxMadeSane.Tests.Infrastructure;

public sealed class HttpsMailDeliveryTests
{
    private const string Sender = "lms@example.test";
    private static readonly byte[] Mime = Encoding.ASCII.GetBytes("Message-ID: <original@example.test>\r\nFrom: LMS <lms@example.test>\r\nTo: one@example.test\r\nCc: two@example.test\r\nReply-To: reply@example.test\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=unique\r\n\r\n--unique\r\nContent-Type: text/plain\r\n\r\nOriginal text\r\n--unique\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=test.bin\r\nContent-Transfer-Encoding: base64\r\n\r\nAAECAwQ=\r\n--unique--\r\n");
    private static OutboundMailMessage Message(string recipient = "one@example.test", string queue = "ABC123") => new(queue, Sender, [recipient], Mime);
    internal static MailRelayOutboundSettings Settings(MailRelayOutboundMode mode = MailRelayOutboundMode.MicrosoftGraph) => new() {
        Mode = mode, Api = new() { SenderAddress = Sender, ClientId = "00000000-0000-0000-0000-000000000001", TenantId = "00000000-0000-0000-0000-000000000002",
            ClientSecretReference = "client", RefreshTokenReference = "refresh", Region = "eu-west-2", SendingIdentity = "example.test" } };

    [Fact]
    public void OldConfigurationDefaultsToTheExistingDirectTransport()
    {
        var old = JsonSerializer.Deserialize<MailRelayOutboundSettings>("{}")!;
        Assert.Equal(MailRelayOutboundMode.DirectPort25, old.Mode); Assert.False(old.UsesHttpsApi);
        var cf = MailRelayProvisioningService.BuildMainCf(MailRelayConfiguration.CreateDefault(DateTimeOffset.UtcNow));
        Assert.Contains("smtp_tcp_port = 25", cf); Assert.DoesNotContain("lmsapi", cf); Assert.Contains("smtpd_milters = inet:", cf);
    }
    [Theory]
    [InlineData("one@example.test", "To:")]
    [InlineData("two@example.test", "Cc:")]
    [InlineData("hidden@example.test", "Bcc:")]
    public void MimeBodyAndAttachmentBytesSurviveEnvelopeRecipientProjection(string recipient, string header)
    {
        var prepared = MailRelayMime.Prepare(Message(recipient), Sender, true);
        var text = Encoding.ASCII.GetString(prepared);
        Assert.Contains(header + (header == "Bcc:" ? " <" + recipient + ">" : " " + recipient), text);
        Assert.DoesNotContain(recipient == "one@example.test" ? "To: <two@example.test>" : "To: one@example.test", text);
        Assert.Contains("Message-ID: <original@example.test>", text); Assert.Contains("Reply-To: reply@example.test", text);
        static byte[] Body(byte[] bytes) => bytes[(bytes.AsSpan().IndexOf("\r\n\r\n"u8) + 4)..];
        Assert.Equal(Body(Mime), Body(prepared));
    }
    [Fact]
    public void SesPreservesRawMimeAndRejectsUnauthorisedSenderOrExpiredSecurityMail()
    {
        Assert.Same(Mime, MailRelayMime.Prepare(Message(), Sender, false));
        Assert.Throws<InvalidDataException>(() => MailRelayMime.Prepare(Message(), "other@example.test", false));
        var expired = Message() with { Mime = Encoding.ASCII.GetBytes("Expires: Thu, 01 Jan 2026 00:00:00 +0000\r\n" + Encoding.ASCII.GetString(Mime)) };
        Assert.Throws<InvalidDataException>(() => MailRelayMime.Prepare(expired, Sender, true));
    }
    [Theory]
    [InlineData(MailRelayOutboundMode.MicrosoftGraph)]
    [InlineData(MailRelayOutboundMode.Gmail)]
    public async Task HttpProvidersSubmitMimeWithExactEnvelopeRecipientAndNoExtraPermissions(MailRelayOutboundMode mode)
    {
        var calls = new List<HttpRequestMessage>();
        var http = new HttpFixture(async request => {
            calls.Add(request);
            Assert.Equal("https", request.RequestUri!.Scheme);
            if (request.RequestUri.AbsolutePath.EndsWith("/token"))
            {
                var body = await request.Content!.ReadAsStringAsync();
                Assert.Contains(mode == MailRelayOutboundMode.Gmail ? "grant_type=refresh_token" : "grant_type=client_credentials", body);
                return Json(HttpStatusCode.OK, "{\"access_token\":\"private-token\",\"expires_in\":3600}");
            }
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            byte[] decoded;
            if (mode == MailRelayOutboundMode.MicrosoftGraph)
            {
                Assert.Contains("/users/lms%40example.test/sendMail", request.RequestUri.AbsoluteUri);
                Assert.Equal("text/plain", request.Content!.Headers.ContentType!.MediaType);
                decoded = Convert.FromBase64String(await request.Content.ReadAsStringAsync());
            }
            else
            {
                var json = await request.Content!.ReadFromJsonAsync<JsonElement>();
                var raw = json.GetProperty("raw").GetString()!; Assert.DoesNotContain("=", raw);
                decoded = DecodeUrl(raw);
            }
            var text = Encoding.ASCII.GetString(decoded);
            Assert.Contains("Bcc: <hidden@example.test>", text); Assert.DoesNotContain("To: one@example.test", text); Assert.Contains("AAECAwQ=", text);
            return mode == MailRelayOutboundMode.MicrosoftGraph ? new(HttpStatusCode.Accepted) : Json(HttpStatusCode.OK, "{\"id\":\"gmail-123\"}");
        });
        var result = await new HttpsOutboundMailProvider(new Secrets(), http).SendAsync(Settings(mode), Message("hidden@example.test"));
        Assert.Equal(MailDeliveryOutcome.Accepted, result.Outcome); Assert.Equal(2, calls.Count);
        Assert.Contains("not yet confirmed", result.Detail);
    }
    [Fact]
    public async Task GraphTargetsResolvedMailboxIdWhenPrimaryEmailDiffersFromUpn()
    {
        const string mailboxId = "f1111111-1111-1111-1111-111111111111";
        var sent = false;
        var http = new HttpFixture(request => {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token")) return Task.FromResult(Json(HttpStatusCode.OK, "{\"access_token\":\"fixture-token\"}"));
            Assert.Equal("/v1.0/users/" + mailboxId + "/sendMail", request.RequestUri.AbsolutePath);
            sent = true; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        });
        var settings = Settings(); settings = settings with { Api = settings.Api with { MicrosoftMailboxObjectId = mailboxId } };
        var result = await new HttpsOutboundMailProvider(new Secrets(), http).SendAsync(settings, Message());
        Assert.True(sent); Assert.Equal(MailDeliveryOutcome.Accepted, result.Outcome);
    }
    [Theory]
    [InlineData("ErrorAccessDenied", true, true)]
    [InlineData("ErrorAccessDenied", false, false)]
    [InlineData("ErrorSendAsDenied", true, false)]
    public async Task GraphDenialRetainsErrorCodeAndExplainsPossiblePropagationOnlyForVerifiedMailboxes(string code, bool verified, bool propagationHint)
    {
        var sends = 0;
        var http = new HttpFixture(request => {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token")) return Task.FromResult(Json(HttpStatusCode.OK, "{\"access_token\":\"fixture-token\"}"));
            sends++; return Task.FromResult(Json(HttpStatusCode.Forbidden, JsonSerializer.Serialize(new { error = new { code, message = "Access denied. Bearer secret-response-token" } })));
        });
        var settings = Settings(); settings = settings with { Api = settings.Api with { MicrosoftSetupStage = verified ? MicrosoftMailSetupStage.MailboxAuthorised : MicrosoftMailSetupStage.None } };
        var result = await new HttpsOutboundMailProvider(new Secrets(), http).SendAsync(settings, Message());
        Assert.Equal(MailDeliveryOutcome.ConfigurationFailure, result.Outcome); Assert.Equal(1, sends);
        Assert.Contains(code, result.Detail); Assert.DoesNotContain("secret-response-token", result.Detail);
        Assert.Equal(propagationHint, result.Detail.Contains("30 minutes"));
        Assert.Null(result.RetryAfterUtc); // No automatic send/fallback based on a guessed cache delay.
    }
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, MailDeliveryOutcome.ConfigurationFailure)]
    [InlineData(HttpStatusCode.TooManyRequests, MailDeliveryOutcome.TemporaryFailure)]
    public async Task RejectedGraphResponseRemainsRejectedWhenItsBodyCannotBeRead(HttpStatusCode status, MailDeliveryOutcome outcome)
    {
        var sends = 0;
        var http = new HttpFixture(request => {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token")) return Task.FromResult(Json(HttpStatusCode.OK, "{\"access_token\":\"fixture-token\"}"));
            sends++; return Task.FromResult(new HttpResponseMessage(status) { Content = new BrokenErrorContent() });
        });
        var result = await new HttpsOutboundMailProvider(new Secrets(), http).SendAsync(Settings(), Message());
        Assert.Equal(outcome, result.Outcome); Assert.Equal(1, sends);
    }
    private sealed class BrokenErrorContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.FromException(new HttpRequestException("Response body unavailable"));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    [Fact]
    public async Task SesSubmitsRawMimeWithExplicitDestinationThroughOfficialSdkContract()
    {
        var proxy = DispatchProxy.Create<IAmazonSimpleEmailServiceV2, SesProxy>();
        ((SesProxy)(object)proxy).Send = request => {
            Assert.Equal(Sender, request.FromEmailAddress); Assert.Equal(new[] { "hidden@example.test" }, request.Destination.ToAddresses);
            Assert.Equal(Mime, request.Content.Raw.Data.ToArray()); return new SendEmailResponse { MessageId = "ses-123" };
        };
        var result = await new HttpsOutboundMailProvider(new Secrets(), new HttpFixture(_ => throw new Exception("No raw HTTP expected")), (_, _) => Task.FromResult(proxy))
            .SendAsync(Settings(MailRelayOutboundMode.AmazonSes), Message("hidden@example.test"));
        Assert.Equal(MailDeliveryOutcome.Accepted, result.Outcome); Assert.Equal("ses-123", result.ProviderMessageId);
    }
    [Theory]
    [InlineData(401, MailDeliveryOutcome.AuthenticationFailure)]
    [InlineData(403, MailDeliveryOutcome.ConfigurationFailure)]
    [InlineData(429, MailDeliveryOutcome.TemporaryFailure)]
    [InlineData(400, MailDeliveryOutcome.PermanentFailure)]
    [InlineData(500, MailDeliveryOutcome.Indeterminate)]
    [InlineData(503, MailDeliveryOutcome.Indeterminate)]
    [InlineData(408, MailDeliveryOutcome.Indeterminate)]
    public void ProviderFailuresHaveQueueSafeClassifications(int status, MailDeliveryOutcome expected)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(3));
        var result = HttpsOutboundMailProvider.ClassifyResponse(response); Assert.Equal(expected, result.Outcome);
        if (status == 429) Assert.True(result.RetryAfterUtc > DateTimeOffset.UtcNow.AddMinutes(2));
    }
    [Fact]
    public async Task RevokedGoogleAuthorisationNeverReachesSendEndpoint()
    {
        var calls = 0; var http = new HttpFixture(_ => { calls++; return Task.FromResult(Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")); });
        var result = await new HttpsOutboundMailProvider(new Secrets(), http).SendAsync(Settings(MailRelayOutboundMode.Gmail), Message());
        Assert.Equal(MailDeliveryOutcome.AuthenticationFailure, result.Outcome); Assert.Equal(1, calls); Assert.Contains("Reconnect", result.Detail);
    }
    [Fact]
    public async Task TimeoutAfterSubmissionIsIndeterminateAndNeverFallsBack()
    {
        var sends = 0; var http = new HttpFixture(request => {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token")) return Task.FromResult(Json(HttpStatusCode.OK, "{\"access_token\":\"private-token\"}"));
            sends++; throw new TaskCanceledException();
        });
        var result = await new HttpsOutboundMailProvider(new Secrets(), http).SendAsync(Settings(), Message());
        Assert.Equal(MailDeliveryOutcome.Indeterminate, result.Outcome); Assert.Equal(1, sends);
    }
    [Fact]
    public async Task ReceiptClaimSurvivesRestartAndPreventsDuplicateOrConcurrentSubmission()
    {
        await using var fixture = await Database.CreateAsync();
        var first = await fixture.Receipts.ClaimAsync("key", "QUEUE", default); Assert.True(first.Claimed);
        var afterCrash = new MailDeliveryReceiptStore(fixture.Db);
        var second = await afterCrash.ClaimAsync("key", "QUEUE", default); Assert.False(second.Claimed); Assert.Equal(MailDeliveryOutcome.Indeterminate, second.Receipt.Outcome);
        await fixture.Receipts.CompleteAsync(first.Receipt, new(MailDeliveryOutcome.Accepted, "accepted", "id-123"), default);
        var accepted = await afterCrash.ClaimAsync("key", "QUEUE", default); Assert.False(accepted.Claimed); Assert.Equal("id-123", accepted.Receipt.ProviderMessageId);
    }
    [Fact]
    public async Task ExplicitThrottleRetriesOnlyAfterDelayAndSuccessfulRetryClearsAttention()
    {
        await using var fixture = await Database.CreateAsync();
        var first = await fixture.Receipts.ClaimAsync("retry", "QUEUE", default);
        await fixture.Receipts.CompleteAsync(first.Receipt, new(MailDeliveryOutcome.TemporaryFailure, "throttled", RetryAfterUtc: DateTimeOffset.UtcNow.AddMinutes(2)), default);
        Assert.False((await fixture.Receipts.ClaimAsync("retry", "QUEUE", default)).Claimed);
        await fixture.Receipts.CompleteAsync(first.Receipt, new(MailDeliveryOutcome.TemporaryFailure, "retry now"), default);
        var retry = await fixture.Receipts.ClaimAsync("retry", "QUEUE", default); Assert.True(retry.Claimed);
        await fixture.Receipts.CompleteAsync(retry.Receipt, new(MailDeliveryOutcome.Accepted, "accepted"), default);
        Assert.Empty(await fixture.Receipts.ListAttentionAsync());
    }
    [Fact]
    public async Task DeliveryEngineReusesQueueIdentityAndDoesNotResendAfterAcceptedOrUncertainResults()
    {
        foreach (var outcome in new[] { MailDeliveryOutcome.Accepted, MailDeliveryOutcome.Indeterminate })
        {
            await using var fixture = await Database.CreateAsync();
            var relay = new SqliteMailRelayStore(fixture.Db);
            await relay.SaveConfigurationAsync(MailRelayConfiguration.CreateDefault(DateTimeOffset.UtcNow) with { Enabled = true, Outbound = Settings() });
            var provider = new RecordingProvider(outcome); var service = new MailRelayApiDeliveryService(relay, fixture.Receipts, provider);
            Assert.Equal(outcome, (await service.DeliverAsync(Message(), default)).Outcome);
            Assert.Equal(outcome, (await service.DeliverAsync(Message(), default)).Outcome); Assert.Equal(1, provider.Sends);
            await service.DeliverAsync(Message("two@example.test"), default); Assert.Equal(2, provider.Sends);
        }
    }
    [Fact]
    public async Task GoogleConsentIsSendOnlyPkceBoundSingleUseAndOwnerBound()
    {
        await using var database = await Database.CreateAsync();
        var services = new ServiceCollection(); services.AddSingleton<ISecretStore>(new Secrets());
        services.AddSingleton(database.Db); services.AddSingleton<MailProviderSetupDraftStore>();
        using var root = services.BuildServiceProvider();
        var posts = 0; var http = new HttpFixture(async request => {
            posts++; var form = await request.Content!.ReadAsStringAsync(); Assert.Contains("code_verifier=", form); Assert.Contains("grant_type=authorization_code", form);
            return Json(HttpStatusCode.OK, "{\"access_token\":\"access\",\"refresh_token\":\"new-refresh\",\"scope\":\"https://www.googleapis.com/auth/gmail.send\"}");
        });
        var google = new GoogleMailAuthorisation(root.GetRequiredService<IServiceScopeFactory>(), http);
        var consent = google.Begin("owner", Settings(MailRelayOutboundMode.Gmail).Api, "https://lms.example.test/auth/mail-relay/google/callback");
        Assert.Contains("code_challenge_method=S256", consent.Url); Assert.Contains("access_type=offline", consent.Url); Assert.DoesNotContain("openid", consent.Url);
        Assert.True(await google.CompleteAsync(consent.State, "code", default)); Assert.False(await google.CompleteAsync(consent.State, "code", default)); Assert.Equal(1, posts);
        Assert.Null(google.GetConnected("other-user", consent.State)); Assert.NotNull(google.GetConnected("owner", consent.State)?.RefreshTokenReference);
        Assert.Throws<InvalidOperationException>(() => google.Begin("owner", Settings().Api, "http://public.example.test/callback"));
    }
    [Fact]
    public void ApiTransportUsesNativeQueueUnprivilegedPipeAndLeavesListenerRulesIntact()
    {
        var config = MailRelayConfiguration.CreateDefault(DateTimeOffset.UtcNow) with { Outbound = Settings() };
        var main = MailRelayProvisioningService.BuildMainCf(config, stageApi: true); var master = MailRelayProvisioningService.BuildMasterCf(["127.0.0.1"], config);
        Assert.Contains("default_transport = lmsapi:localhost", main); Assert.Contains("defer_transports = lmsapi", main);
        Assert.Contains("lmsapi_destination_recipient_limit = 50", main); Assert.Contains("user=lmsmailapi", master); Assert.Contains("${queue_id}", master);
        Assert.Contains("smtpd_relay_restrictions=permit_sasl_authenticated,reject", master); Assert.Contains("eol=\\r\\n", master);
        var method = typeof(MailRelayProvisioningService).GetMethod("BuildImageContext", BindingFlags.NonPublic | BindingFlags.Static)!;
        var archive = (byte[])method.Invoke(null, null)!;
        var directory = Environment.GetEnvironmentVariable("LMS_MAIL_TRANSPORT_FIXTURE_DIR");
        if (directory is not null) { Directory.CreateDirectory(directory); File.WriteAllBytes(Path.Combine(directory, "context.tar"), archive); File.WriteAllText(Path.Combine(directory, "main.cf"), MailRelayProvisioningService.BuildMainCf(config)); File.WriteAllText(Path.Combine(directory, "master-lms.cf"), master); }
        using var reader = new TarReader(new MemoryStream(archive)); var entries = new Dictionary<string, string>();
        while (reader.GetNextEntry() is { } entry) { using var text = new StreamReader(entry.DataStream!); entries[entry.Name] = text.ReadToEnd(); }
        Assert.Contains("NoRedirect", entries["postfix-api.py"]); Assert.Contains("return 0 if outcome == 0 else 69 if outcome == 2 else 75", entries["postfix-api.py"]);
        Assert.Contains("chmod 0644 /usr/local/bin/lms-mail-api", entries["Dockerfile"]);
    }
    [Fact]
    public async Task MicrosoftCertificateAssertionUsesTheRegisteredKeyAndShortLivedAudienceBoundClaims()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=LMS fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var secrets = new Secrets(); var reference = await secrets.StoreSecretAsync(Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12)), "fixture");
        var http = new HttpFixture(async message => {
            var body = await message.Content!.ReadAsStringAsync();
            var form = body.Split('&').Select(v => v.Split('=',2)).ToDictionary(v => Uri.UnescapeDataString(v[0]), v => Uri.UnescapeDataString(v[1]));
            Assert.False(form.ContainsKey("client_secret"));
            var jwt = form["client_assertion"].Split('.');
            using var header = JsonDocument.Parse(DecodeUrl(jwt[0])); Assert.Equal("PS256", header.RootElement.GetProperty("alg").GetString());
            using var claims = JsonDocument.Parse(DecodeUrl(jwt[1])); Assert.Equal(message.RequestUri!.AbsoluteUri, claims.RootElement.GetProperty("aud").GetString());
            Assert.InRange(claims.RootElement.GetProperty("exp").GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 290, 310);
            Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), DecodeUrl(jwt[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
            return Json(HttpStatusCode.OK, "{\"access_token\":\"token\",\"expires_in\":3600}");
        });
        using var client = http.CreateClient("fixture");
        var acquired = await GraphApplicationAuthentication.AcquireAsync(client, secrets, MessagingEmailSettings.CreateDefault(DateTimeOffset.UtcNow) with {
            GraphTenantId = Settings().Api.TenantId, GraphClientId = Settings().Api.ClientId }, default, reference);
        Assert.Equal("token", acquired.Token);
    }
    [Fact]
    public async Task ExplicitUncertainResolutionAllowsOnlyTheChosenSafeNextAction()
    {
        await using var fixture = await Database.CreateAsync();
        var claim = await fixture.Receipts.ClaimAsync("uncertain", "QUEUE", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Receipts.ResolveAsync("uncertain", false));
        var old = claim.Receipt with { UpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-10) };
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_delivery_receipts SET ReceiptJson={JsonSerializer.Serialize(old)} WHERE Key={old.Key}");
        await fixture.Receipts.ResolveAsync("uncertain", false);
        Assert.True((await fixture.Receipts.ClaimAsync("uncertain", "QUEUE", default)).Claimed);
    }
    [Fact]
    public async Task ParallelNativeRetriesCannotClaimTheSameDeliveryTwice()
    {
        await using var fixture = await Database.CreateAsync();
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => fixture.Receipts.ClaimAsync("same", "QUEUE", default))));
        Assert.Single(claims, c => c.Claimed);
    }
    [Fact]
    public async Task SetupDraftCanBeResumedClearedAndCreatedAgainWithoutStaleTracking()
    {
        await using var fixture = await Database.CreateAsync(); var drafts = new MailProviderSetupDraftStore(fixture.Db);
        var original = Settings(); await drafts.SaveAsync(original);
        Assert.Equal(original.Api, (await drafts.GetAsync(original.Mode))!.Api);
        await drafts.ClearAsync(original.Mode); Assert.Null(await drafts.GetAsync(original.Mode));
        await drafts.SaveAsync(original with { Api = original.Api with { ClientId = "new-client" } });
        Assert.Equal("new-client", (await drafts.GetAsync(original.Mode))!.Api.ClientId);
    }
    [Fact]
    public void OrdinaryBatchesPreserveToCcAndIncludeOnlyBlindEnvelopeRecipientsInBcc()
    {
        var message = Message() with { Recipients = ["one@example.test", "two@example.test", "hidden@example.test"] };
        var text = Encoding.ASCII.GetString(MailRelayMime.Prepare(message, Sender, true));
        Assert.Contains("To: one@example.test\r\n", text); Assert.Contains("Cc: two@example.test\r\n", text);
        Assert.Contains("Bcc: <hidden@example.test>", text); Assert.Contains("AAECAwQ=", text);
    }
    [Fact]
    public async Task BatchOrderingDoesNotChangeReceiptIdentityAndRegroupingCannotDuplicateRecipients()
    {
        await using var fixture = await Database.CreateAsync(); var relay = new SqliteMailRelayStore(fixture.Db);
        await relay.SaveConfigurationAsync(MailRelayConfiguration.CreateDefault(DateTimeOffset.UtcNow) with { Enabled = true, Outbound = Settings() });
        var provider = new RecordingProvider(MailDeliveryOutcome.Accepted); var engine = new MailRelayApiDeliveryService(relay, fixture.Receipts, provider);
        var batch = Message() with { Recipients = ["one@example.test", "two@example.test"] };
        Assert.Equal(MailDeliveryOutcome.Accepted, (await engine.DeliverAsync(batch, default)).Outcome);
        Assert.Equal(MailDeliveryOutcome.Accepted, (await engine.DeliverAsync(batch with { Recipients = batch.Recipients.Reverse().ToArray() }, default)).Outcome);
        Assert.Equal(1, provider.Sends);
        var changed = batch with { Recipients = ["two@example.test", "hidden@example.test"] };
        Assert.Equal(MailDeliveryOutcome.Indeterminate, (await engine.DeliverAsync(changed, default)).Outcome); Assert.Equal(1, provider.Sends);
    }
    [Theory]
    [InlineData("undisclosed-recipients:;")]
    [InlineData("Team: one@example.test, two@example.test;")]
    public void RfcAddressGroupsAndBlindOnlyMailPreserveMimeBody(string recipients)
    {
        var raw = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Mime).Replace("To: one@example.test", "To: " + recipients));
        var message = Message() with { Mime = raw, Recipients = ["one@example.test", "two@example.test", "hidden@example.test"] };
        var text = Encoding.ASCII.GetString(MailRelayMime.Prepare(message, Sender, true));
        Assert.Contains("To: " + recipients, text); Assert.Contains("AAECAwQ=", text); Assert.Contains("hidden@example.test", text);
    }
    [Fact]
    public async Task MultipleConnectionsOfTheSameProviderAreSavedIndependentlyOfTheLiveRoute()
    {
        await using var fixture = await Database.CreateAsync(); var relay = new SqliteMailRelayStore(fixture.Db);
        var live = MailRelayConfiguration.CreateDefault(DateTimeOffset.UtcNow) with { Enabled = true };
        await relay.SaveConfigurationAsync(live); var before = JsonSerializer.Serialize(await relay.GetConfigurationAsync());
        var store = new MailProviderConnectionStore(fixture.Db);
        var first = new MailProviderConnection(Guid.NewGuid(), "Microsoft test A", Settings(), DateTimeOffset.UtcNow);
        var second = new MailProviderConnection(Guid.NewGuid(), "Microsoft test B", Settings() with { Api = Settings().Api with { SenderAddress = "other@example.test" } }, DateTimeOffset.UtcNow);
        await store.SaveAsync(first); await store.SaveAsync(second);
        Assert.Equal(2, (await store.ListAsync()).Count);
        Assert.Equal(before, JsonSerializer.Serialize(await relay.GetConfigurationAsync()));
        await store.SaveAsync(first with { Name = "Renamed A" }); Assert.Equal(2, (await store.ListAsync()).Count);
        Assert.Contains(await store.ListAsync(), c => c.Id == second.Id && c.Name == "Microsoft test B");
        await store.PreserveLiveAsync(live); await store.PreserveLiveAsync(live);
        Assert.Equal(3, (await store.ListAsync()).Count); Assert.Equal(before, JsonSerializer.Serialize(await relay.GetConfigurationAsync()));
    }
    private static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static byte[] DecodeUrl(string value) { value = value.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '=')); }
    private sealed class HttpFixture(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
    private sealed class Secrets : ISecretStore
    {
        private readonly Dictionary<string, string> values = new() { ["client"] = "client-secret", ["refresh"] = "refresh-secret" };
        public Task<string?> ResolveSecretAsync(string reference, CancellationToken token = default) => Task.FromResult(values.GetValueOrDefault(reference));
        public Task<string> StoreSecretAsync(string value, string purpose, CancellationToken token = default) { var key = Guid.NewGuid().ToString("N"); values[key] = value; return Task.FromResult(key); }
        public Task DeleteSecretAsync(string reference, CancellationToken token = default) { values.Remove(reference); return Task.CompletedTask; }
        public Task<SecretReferenceMetadata?> GetMetadataAsync(string reference, CancellationToken token = default) => Task.FromResult<SecretReferenceMetadata?>(null);
    }
    public class SesProxy : DispatchProxy
    {
        public Func<SendEmailRequest, SendEmailResponse> Send = _ => new();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch { "SendEmailAsync" => Task.FromResult(Send((SendEmailRequest)args![0]!)), "Dispose" => null, _ => throw new NotSupportedException(method.Name) };
    }
    private sealed class RecordingProvider(MailDeliveryOutcome outcome) : IOutboundMailProvider
    {
        public int Sends;
        public Task<MailDeliveryResult> SendAsync(MailRelayOutboundSettings settings, OutboundMailMessage message, CancellationToken token = default) { Sends++; return Task.FromResult(new MailDeliveryResult(outcome, "provider result")); }
        public Task<MailDeliveryResult> CheckAsync(MailRelayOutboundSettings settings, CancellationToken token = default) => throw new NotImplementedException();
    }
    private sealed class Database(string path, LinuxMadeSaneDbContext db) : IAsyncDisposable
    {
        public LinuxMadeSaneDbContext Db => db;
        public MailDeliveryReceiptStore Receipts { get; } = new(db);
        public static async Task<Database> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), "lms-mail-" + Guid.NewGuid().ToString("N") + ".db");
            var db = new LinuxMadeSaneDbContext(new DbContextOptionsBuilder<LinuxMadeSaneDbContext>().UseSqlite("Data Source=" + path).Options);
            await db.Database.EnsureCreatedAsync(); await db.Database.ExecuteSqlRawAsync(MailDeliveryReceiptStore.Schema); return new(path, db);
        }
        public async ValueTask DisposeAsync() { await db.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); }
    }
}
