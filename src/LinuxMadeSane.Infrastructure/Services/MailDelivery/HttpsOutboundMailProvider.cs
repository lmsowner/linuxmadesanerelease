// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Google.Apis.Auth.OAuth2;
using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.MailRelay;
using LinuxMadeSane.Core.Models.Messaging;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed partial class HttpsOutboundMailProvider(ISecretStore secrets, IHttpClientFactory clients, Func<MailRelayApiSettings, CancellationToken, Task<IAmazonSimpleEmailServiceV2>>? sesFactory = null, Func<MailRelayApiSettings, CancellationToken, Task<Amazon.SecurityToken.IAmazonSecurityTokenService>>? stsFactory = null) : IOutboundMailProvider
{
    public const string GmailScope = "https://www.googleapis.com/auth/gmail.send";
    public static string? Validate(MailRelayOutboundSettings settings)
    {
        var api = settings.Api;
        if (!settings.UsesHttpsApi) return "Choose an HTTPS delivery provider.";
        if (!MailAddress.TryCreate(api.SenderAddress, out var sender) || sender.Address != api.SenderAddress)
            return "Enter the sending mailbox address without a display name.";
        if (settings.Mode == MailRelayOutboundMode.MicrosoftGraph &&
            (!Guid.TryParse(api.TenantId, out _) || !Guid.TryParse(api.ClientId, out _) || (string.IsNullOrWhiteSpace(api.ClientSecretReference) && string.IsNullOrWhiteSpace(api.CertificateSecretReference))))
            return "Enter the tenant ID, application ID and application credential, or reuse your existing Microsoft email settings.";
        if (settings.Mode == MailRelayOutboundMode.Gmail && (api.WorkspaceDelegation
            ? string.IsNullOrWhiteSpace(api.ServiceAccountSecretReference)
            : string.IsNullOrWhiteSpace(api.ClientId) || string.IsNullOrWhiteSpace(api.ClientSecretReference) || string.IsNullOrWhiteSpace(api.RefreshTokenReference)))
            return api.WorkspaceDelegation ? "Add the authorised Workspace service account and delegated mailbox." : "Connect your Google account to authorise sending.";
        if (settings.Mode == MailRelayOutboundMode.AmazonSes &&
            (api.Region.Length is < 5 or > 40 || api.Region.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || string.IsNullOrWhiteSpace(api.SendingIdentity)))
            return "Choose an AWS region and verified SES sending identity.";
        if (settings.Mode == MailRelayOutboundMode.AmazonSes && !(api.SendingIdentity.Equals(api.SenderAddress, StringComparison.OrdinalIgnoreCase) ||
            sender.Host.Equals(api.SendingIdentity, StringComparison.OrdinalIgnoreCase) || sender.Host.EndsWith("." + api.SendingIdentity, StringComparison.OrdinalIgnoreCase)))
            return "The sender must belong to the selected SES verified email or domain identity.";
        return null;
    }

    public async Task<MailDeliveryResult> CheckAsync(MailRelayOutboundSettings settings, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45)); cancellationToken = timeout.Token;
        if (Validate(settings) is { } error) return new(MailDeliveryOutcome.ConfigurationFailure, error);
        try
        {
            if (settings.Mode == MailRelayOutboundMode.AmazonSes)
            {
                using var ses = await CreateSesAsync(settings.Api, cancellationToken);
                var account = await ses.GetAccountAsync(new GetAccountRequest(), cancellationToken);
                var identity = await ses.GetEmailIdentityAsync(new GetEmailIdentityRequest { EmailIdentity = settings.Api.SendingIdentity }, cancellationToken);
                if (account.SendingEnabled != true || identity.VerifiedForSendingStatus != true)
                    return new(MailDeliveryOutcome.ConfigurationFailure, "SES sending is disabled or the selected identity is not verified in this region. Complete verification, then check again.");
                var detail = account.ProductionAccessEnabled == true ? "SES identity verified; production access enabled." : "SES identity verified; account is in the sandbox. Recipients must also be verified until AWS approves production access.";
                return new(MailDeliveryOutcome.Accepted, detail + " A send test is still required to validate sending permission.");
            }
            _ = await AccessTokenAsync(settings, cancellationToken);
            return new(MailDeliveryOutcome.Accepted, "Application sign-in verified. Send a test to validate mailbox permissions and sender authorisation; sign-in alone does not prove sending access.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        { return ClassifyException(exception, submitted: false); }
    }

    public async Task<MailDeliveryResult> SendAsync(MailRelayOutboundSettings settings, OutboundMailMessage message, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45)); cancellationToken = timeout.Token;
        if (Validate(settings) is { } error) return new(MailDeliveryOutcome.ConfigurationFailure, error);
        var maxMimeBytes = settings.Mode == MailRelayOutboundMode.MicrosoftGraph ? 2_800_000 : 26_214_400;
        if (message.Mime.Length > maxMimeBytes) return new(MailDeliveryOutcome.PermanentFailure, "Message exceeds this provider transport's MIME size limit, including attachment encoding.");
        if (message.Recipients.Length is < 1 or > 50 || message.Recipients.Any(r => !MailAddress.TryCreate(r, out _)))
            return new(MailDeliveryOutcome.ConfigurationFailure, "The native queue transport must submit one envelope recipient per delivery attempt.");
        // MIME Graph submissions are base64 inside the request's 4 MB ceiling.
        var limit = settings.Mode == MailRelayOutboundMode.MicrosoftGraph ? 2_800_000 : 25_000_000;
        if (message.Mime.Length > limit) return new(MailDeliveryOutcome.PermanentFailure, $"Message exceeds this delivery route's {limit / 1_000_000.0:0.#} MB MIME limit. Use another route for larger messages.");
        byte[] mime;
        try { mime = MailRelayMime.Prepare(message, settings.Api.SenderAddress, settings.Mode != MailRelayOutboundMode.AmazonSes); }
        catch (InvalidDataException exception) { return new(MailDeliveryOutcome.PermanentFailure, exception.Message); }
        var submitted = false;
        try
        {
            if (settings.Mode == MailRelayOutboundMode.AmazonSes)
            {
                using var ses = await CreateSesAsync(settings.Api, cancellationToken);
                submitted = true;
                var sesResponse = await ses.SendEmailAsync(new SendEmailRequest {
                    FromEmailAddress = settings.Api.SenderAddress,
                    Destination = new Destination { ToAddresses = message.Recipients.ToList() },
                    Content = new EmailContent { Raw = new RawMessage { Data = new MemoryStream(mime, writable: false) } }
                }, cancellationToken);
                return new(MailDeliveryOutcome.Accepted, "Accepted by Amazon SES for delivery; recipient delivery is not yet confirmed.", sesResponse.MessageId);
            }
            var accessToken = await AccessTokenAsync(settings, cancellationToken);
            var graph = settings.Mode == MailRelayOutboundMode.MicrosoftGraph;
            using var request = new HttpRequestMessage(HttpMethod.Post, graph
                ? "https://graph.microsoft.com/v1.0/users/" + Uri.EscapeDataString(Guid.TryParse(settings.Api.MicrosoftMailboxObjectId, out var mailboxId) && mailboxId != Guid.Empty ? mailboxId.ToString() : settings.Api.SenderAddress) + "/sendMail"
                : "https://gmail.googleapis.com/gmail/v1/users/me/messages/send");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = graph
                ? new StringContent(Convert.ToBase64String(mime), Encoding.ASCII, "text/plain")
                : JsonContent.Create(new { raw = Convert.ToBase64String(mime).TrimEnd('=').Replace('+', '-').Replace('/', '_') });
            using var client = clients.CreateClient(nameof(HttpsOutboundMailProvider));
            submitted = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return await ClassifyApiResponseAsync(settings, response, cancellationToken);
            // Once accepted, malformed/missing response metadata must not trigger another send.
            string? id = null;
            if (!graph)
            {
                try { var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken); id = json.TryGetProperty("id", out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() : null : null; }
                catch (Exception exception) when (exception is JsonException or IOException or OperationCanceledException) { }
            }
            return new(MailDeliveryOutcome.Accepted, "Accepted by provider for delivery; recipient delivery is not yet confirmed.", id);
        }
        catch (Exception exception) { return ClassifyException(exception, submitted); }
    }

    private async Task<string> AccessTokenAsync(MailRelayOutboundSettings settings, CancellationToken token)
    {
        var api = settings.Api;
        if (settings.Mode == MailRelayOutboundMode.MicrosoftGraph)
        {
            using var client = clients.CreateClient(nameof(HttpsOutboundMailProvider));
            var acquired = await GraphApplicationAuthentication.AcquireAsync(client, secrets,
                MessagingEmailSettings.CreateDefault(DateTimeOffset.UtcNow) with { GraphTenantId = api.TenantId, GraphClientId = api.ClientId, GraphClientSecretReference = api.ClientSecretReference }, token, api.CertificateSecretReference);
            return acquired.Token ?? throw new MailProviderAuthenticationException("Microsoft returned no access token. Reconnect Microsoft 365.");
        }
        if (api.WorkspaceDelegation)
        {
            var json = await SecretAsync(api.ServiceAccountSecretReference, token);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.GetProperty("type").GetString() != "service_account" ||
                document.RootElement.GetProperty("token_uri").GetString() != "https://oauth2.googleapis.com/token")
                throw new MailProviderAuthenticationException("Use a Google service-account key authorised for Workspace delegation.");
#pragma warning disable CS0618 // The supplied key is validated above; only service-account credentials are accepted.
            var credential = GoogleCredential.FromJson(json).CreateScoped(GmailScope).CreateWithUser(api.SenderAddress);
#pragma warning restore CS0618
            return await credential.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: token);
        }
        using var googleClient = clients.CreateClient(nameof(HttpsOutboundMailProvider));
        using var response = await googleClient.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["client_id"] = api.ClientId, ["client_secret"] = await SecretAsync(api.ClientSecretReference, token),
            ["refresh_token"] = await SecretAsync(api.RefreshTokenReference, token), ["grant_type"] = "refresh_token" }), token);
        if (!response.IsSuccessStatusCode)
        {
            var temporary = (int)response.StatusCode == 429 || (int)response.StatusCode >= 500;
            throw new MailProviderAuthenticationException(temporary ? "Google sign-in is temporarily unavailable. The queue will retry." : "Google authorisation expired or was revoked. Reconnect Google Account; check OAuth app publishing status and consent policy.") { Transient = temporary };
        }
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        return result.GetProperty("access_token").GetString() ?? throw new MailProviderAuthenticationException("Google returned no access token. Reconnect Google Account.");
    }
    private async Task<string> SecretAsync(string? reference, CancellationToken token) =>
        string.IsNullOrWhiteSpace(reference) ? throw new MailProviderAuthenticationException("A required credential is missing. Reconnect this provider.")
        : await secrets.ResolveSecretAsync(reference, token) ?? throw new MailProviderAuthenticationException("A stored credential could not be read. Reconnect this provider.");

    private async Task<IAmazonSimpleEmailServiceV2> CreateSesAsync(MailRelayApiSettings settings, CancellationToken token)
    {
        if (sesFactory is not null) return await sesFactory(settings, token);
        var config = new AmazonSimpleEmailServiceV2Config { RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region), MaxErrorRetry = 0, Timeout = TimeSpan.FromSeconds(45) };
        var credentials = await AwsCredentialsAsync(settings, token);
        return credentials is null ? new AmazonSimpleEmailServiceV2Client(config) : new AmazonSimpleEmailServiceV2Client(credentials, config);
    }
    private async Task<AWSCredentials?> AwsCredentialsAsync(MailRelayApiSettings settings, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(settings.AwsCredentialsSecretReference)) return null;
        var json = JsonSerializer.Deserialize<AwsMailCredentials>(await SecretAsync(settings.AwsCredentialsSecretReference, token))
            ?? throw new MailProviderAuthenticationException("AWS credentials could not be read. Save them again.");
        return string.IsNullOrEmpty(json.SessionToken) ? new BasicAWSCredentials(json.AccessKeyId, json.SecretAccessKey)
            : new SessionAWSCredentials(json.AccessKeyId, json.SecretAccessKey, json.SessionToken);
    }
    private static async Task<MailDeliveryResult> ClassifyApiResponseAsync(MailRelayOutboundSettings settings, HttpResponseMessage response, CancellationToken token)
    {
        var result = ClassifyResponse(response);
        if (settings.Mode != MailRelayOutboundMode.MicrosoftGraph) return result;
        string code = "", message = "";
        try
        {
            // Only inspect a bounded rejected response. A body-read failure cannot turn
            // an explicit rejection into an ambiguous send or trigger a second send.
            using var bodyCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            bodyCancellation.CancelAfter(TimeSpan.FromSeconds(3));
            using var stream = await response.Content.ReadAsStreamAsync(bodyCancellation.Token);
            var bytes = new byte[16384]; var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), bodyCancellation.Token);
                if (read == 0) break;
                count += read;
            }
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var candidate = value.GetString() ?? "";
                    if (System.Text.RegularExpressions.Regex.IsMatch(candidate, @"\A[A-Za-z0-9_.-]{1,80}\z")) code = candidate;
                }
                if (error.TryGetProperty("message", out var detail) && detail.ValueKind == JsonValueKind.String)
                    message = MicrosoftMailRegistration.RedactExchangeResponse(detail.GetString() ?? "");
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or HttpRequestException or OperationCanceledException or InvalidOperationException) { }
        if (string.IsNullOrEmpty(code)) return result;
        var graphDetail = string.IsNullOrWhiteSpace(message) ? code : code + ": " + (message.Length > 240 ? message[..240] + "…" : message);
        if (response.StatusCode == HttpStatusCode.Forbidden && settings.Api.MicrosoftSetupStage == MicrosoftMailSetupStage.MailboxAuthorised &&
            (code.Equals("ErrorAccessDenied", StringComparison.OrdinalIgnoreCase) || code.Equals("Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase)))
            return result with { Detail = $"Microsoft Graph: {graphDetail}. Exchange has verified this mailbox's permission. If setup finished less than 2 hours ago, allow 30 minutes without send attempts before retrying Send test email. For older setups, review mailbox access and policy. Keep this registration." };
        return result with { Detail = $"{result.Detail} Microsoft Graph: {graphDetail}" };
    }

    internal static MailDeliveryResult ClassifyResponse(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status == 429) return new(MailDeliveryOutcome.TemporaryFailure, "Provider rate limit reached. The existing queue will retry after the provider's delay.", RetryAfterUtc:
            response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1)));
        if (status == 401) return new(MailDeliveryOutcome.AuthenticationFailure, "Provider sign-in was rejected. Reconnect the account.");
        if (status == 403) return new(MailDeliveryOutcome.ConfigurationFailure, "Provider denied sending. Check mailbox access, sending limits and sender identity authorisation.");
        // A gateway/server error does not prove that the downstream send was rejected.
        if (status >= 500 || status == 408) return new(MailDeliveryOutcome.Indeterminate, "Provider did not confirm whether it accepted this message. Automatic resending is paused to prevent duplicates. Check provider mail tracing, then resolve this attempt in LMS.");
        return new(MailDeliveryOutcome.PermanentFailure, $"Provider rejected the message (HTTP {status}). Check the sending identity, recipients and message format.");
    }
    internal static MailDeliveryResult ClassifyException(Exception exception, bool submitted) => exception switch {
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => new(MailDeliveryOutcome.TemporaryFailure, "Provider DNS could not be resolved. No message was submitted; the queue will retry."),
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => new(MailDeliveryOutcome.ConfigurationFailure, "Provider TLS could not be verified. Check the host certificate trust and network configuration; no message was submitted."),
        HttpRequestException http when http.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.NetworkUnreachable or SocketError.HostUnreachable } => new(MailDeliveryOutcome.TemporaryFailure, "Provider connection could not be opened. The queue will retry."),
        MailProviderAuthenticationException auth when auth.Transient => new(MailDeliveryOutcome.TemporaryFailure, auth.Message, RetryAfterUtc: DateTimeOffset.UtcNow.AddMinutes(1)),
        MailProviderAuthenticationException => new(MailDeliveryOutcome.AuthenticationFailure, exception.Message),
        AmazonSimpleEmailServiceV2Exception aws when aws.ErrorCode is "TooManyRequestsException" or "ThrottlingException" or "LimitExceededException" => new(MailDeliveryOutcome.TemporaryFailure, "SES sending rate or quota reached. The existing queue will retry.", RetryAfterUtc: DateTimeOffset.UtcNow.AddMinutes(1)),
        AmazonServiceException aws when aws.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(MailDeliveryOutcome.AuthenticationFailure, "AWS denied access. Check the role or IAM credentials and SES sending policy."),
        AmazonSimpleEmailServiceV2Exception aws when (int)aws.StatusCode is >= 400 and < 500 => new(MailDeliveryOutcome.PermanentFailure, "SES rejected this message. Check sender/recipient verification, sandbox status and MIME limits."),
        _ => new(submitted ? MailDeliveryOutcome.Indeterminate : MailDeliveryOutcome.ConfigurationFailure, submitted
            ? "Delivery outcome is uncertain. Automatic resending is paused to prevent duplicates. Check provider mail tracing, then resolve this attempt in LMS."
            : "Could not validate provider access. Check credentials, network connectivity and provider configuration; no message was submitted.")
    };
}
public sealed record AwsMailCredentials(string AccessKeyId, string SecretAccessKey, string? SessionToken);
