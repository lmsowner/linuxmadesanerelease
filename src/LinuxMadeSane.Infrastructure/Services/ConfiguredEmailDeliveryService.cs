// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Messaging;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class ConfiguredEmailDeliveryService(
    IMessagingEmailSettingsStore settingsStore,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory) : IEmailDeliveryService
{
    private readonly SemaphoreSlim graphTokenLock = new(1, 1);
    private string? graphAccessToken;
    private DateTimeOffset graphAccessTokenExpiresAtUtc = DateTimeOffset.MinValue;

    public async Task<EmailDeliveryResult> SendHtmlAsync(
        string recipientAddress,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        if (!settings.IsEnabled || settings.Provider == MessagingEmailProvider.Disabled)
        {
            return new EmailDeliveryResult(false, false, "Email delivery is disabled.");
        }

        if (!MailAddress.TryCreate(recipientAddress?.Trim(), out var recipient))
        {
            return new EmailDeliveryResult(false, false, "Enter a valid destination email address.");
        }

        if (!MailAddress.TryCreate(settings.SenderAddress?.Trim(), out var sender))
        {
            return new EmailDeliveryResult(false, false, "Configure a valid sender email address first.");
        }

        return settings.Provider switch
        {
            MessagingEmailProvider.Smtp => await SendSmtpAsync(settings, sender, recipient, subject, htmlBody, cancellationToken),
            MessagingEmailProvider.MicrosoftGraph => await SendGraphAsync(settings, recipient.Address, subject, htmlBody, cancellationToken),
            _ => new EmailDeliveryResult(false, false, "Choose an email provider first.")
        };
    }

    private async Task<EmailDeliveryResult> SendSmtpAsync(
        MessagingEmailSettings settings,
        MailAddress sender,
        MailAddress recipient,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
        {
            return new EmailDeliveryResult(false, false, "SMTP host is required.");
        }

        using var message = new MailMessage
        {
            From = new MailAddress(sender.Address, settings.SenderDisplayName.Trim()),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };
        message.To.Add(recipient);

        using var client = new SmtpClient(settings.SmtpHost.Trim(), Math.Clamp(settings.SmtpPort, 1, 65535))
        {
            EnableSsl = settings.SmtpUseStartTls,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        if (!string.IsNullOrWhiteSpace(settings.SmtpUsername))
        {
            var password = string.IsNullOrWhiteSpace(settings.SmtpPasswordSecretReference)
                ? string.Empty
                : await secretStore.ResolveSecretAsync(settings.SmtpPasswordSecretReference, cancellationToken) ?? string.Empty;
            client.Credentials = new NetworkCredential(settings.SmtpUsername.Trim(), password);
        }

        try
        {
            await client.SendMailAsync(message, cancellationToken);
            return new EmailDeliveryResult(true, true, $"Email accepted by SMTP for {recipient.Address}.");
        }
        catch (Exception exception)
        {
            return new EmailDeliveryResult(false, true, $"SMTP send failed: {exception.Message}");
        }
    }

    private async Task<EmailDeliveryResult> SendGraphAsync(
        MessagingEmailSettings settings,
        string recipientAddress,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.GraphTenantId) ||
            string.IsNullOrWhiteSpace(settings.GraphClientId) ||
            string.IsNullOrWhiteSpace(settings.GraphClientSecretReference))
        {
            return new EmailDeliveryResult(false, false, "Microsoft Graph tenant id, client id, and client secret are required.");
        }

        var tokenResult = await GetGraphAccessTokenAsync(settings, cancellationToken);
        if (!tokenResult.Succeeded || string.IsNullOrWhiteSpace(tokenResult.Token))
        {
            return new EmailDeliveryResult(false, false, tokenResult.Message);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildGraphSendMailEndpoint(settings.GraphBaseUrl, settings.SenderAddress));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenResult.Token);
        request.Content = JsonContent.Create(new
        {
            message = new
            {
                subject,
                body = new
                {
                    contentType = "HTML",
                    content = htmlBody
                },
                toRecipients = new[]
                {
                    new
                    {
                        emailAddress = new
                        {
                            address = recipientAddress
                        }
                    }
                }
            },
            saveToSentItems = settings.GraphSaveToSentItems
        });

        using var client = httpClientFactory.CreateClient(nameof(ConfiguredEmailDeliveryService));
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return new EmailDeliveryResult(true, true, $"Email accepted by Microsoft Graph for {recipientAddress}.");
        }

        var failure = await ReadFailureMessageAsync(response, cancellationToken);
        return new EmailDeliveryResult(
            false,
            true,
            $"Microsoft Graph sendMail failed with {(int)response.StatusCode} {response.ReasonPhrase}: {failure}");
    }

    private async Task<GraphAccessTokenResult> GetGraphAccessTokenAsync(
        MessagingEmailSettings settings,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(graphAccessToken) && graphAccessTokenExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            return new GraphAccessTokenResult(true, graphAccessToken, "Using cached Microsoft Graph token.");
        }

        await graphTokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(graphAccessToken) && graphAccessTokenExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                return new GraphAccessTokenResult(true, graphAccessToken, "Using cached Microsoft Graph token.");
            }

            var clientSecret = await secretStore.ResolveSecretAsync(settings.GraphClientSecretReference!, cancellationToken);
            if (string.IsNullOrWhiteSpace(clientSecret))
            {
                return new GraphAccessTokenResult(false, null, "Microsoft Graph client secret could not be resolved. Save it again.");
            }

            using var client = httpClientFactory.CreateClient(nameof(ConfiguredEmailDeliveryService));
            using var response = await client.PostAsync(
                BuildGraphTokenEndpoint(settings.GraphAuthority, settings.GraphTenantId),
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = settings.GraphClientId.Trim(),
                    ["client_secret"] = clientSecret,
                    ["scope"] = BuildGraphScope(settings.GraphBaseUrl),
                    ["grant_type"] = "client_credentials"
                }),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var failure = await ReadFailureMessageAsync(response, cancellationToken);
                return new GraphAccessTokenResult(
                    false,
                    null,
                    $"Microsoft Graph token request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {failure}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("access_token", out var tokenElement) ||
                string.IsNullOrWhiteSpace(tokenElement.GetString()))
            {
                return new GraphAccessTokenResult(false, null, "Microsoft Graph token response did not include access_token.");
            }

            var expiresInSeconds = root.TryGetProperty("expires_in", out var expiresInElement) &&
                                   expiresInElement.TryGetInt32(out var parsedExpiresIn)
                ? parsedExpiresIn
                : 3600;

            graphAccessToken = tokenElement.GetString();
            graphAccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresInSeconds - 60));
            return new GraphAccessTokenResult(true, graphAccessToken, "Microsoft Graph token acquired.");
        }
        finally
        {
            graphTokenLock.Release();
        }
    }

    private static string BuildGraphTokenEndpoint(string authorityBaseUrl, string tenantId) =>
        $"{authorityBaseUrl.Trim().TrimEnd('/')}/{tenantId.Trim()}/oauth2/v2.0/token";

    private static string BuildGraphSendMailEndpoint(string graphBaseUrl, string senderAddress) =>
        $"{graphBaseUrl.Trim().TrimEnd('/')}/users/{Uri.EscapeDataString(senderAddress.Trim())}/sendMail";

    private static string BuildGraphScope(string graphBaseUrl)
    {
        var graphBaseUri = new Uri(graphBaseUrl.Trim(), UriKind.Absolute);
        return $"{graphBaseUri.GetLeftPart(UriPartial.Authority)}/.default";
    }

    private static async Task<string> ReadFailureMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return "The response body was empty.";
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error_description", out var errorDescription))
            {
                return errorDescription.GetString() ?? body;
            }

            if (document.RootElement.TryGetProperty("error", out var errorElement))
            {
                if (errorElement.ValueKind == JsonValueKind.String)
                {
                    return errorElement.GetString() ?? body;
                }

                if (errorElement.ValueKind == JsonValueKind.Object &&
                    errorElement.TryGetProperty("message", out var message))
                {
                    return message.GetString() ?? body;
                }
            }
        }
        catch (JsonException)
        {
        }

        return body;
    }

    private sealed record GraphAccessTokenResult(bool Succeeded, string? Token, string Message);
}
