// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Services.EdgeGateway;

public sealed class LmsServerEmailSetupService(
    IEdgeGatewayService gateway,
    IMailRelayService relay,
    IMailRelayStore relayStore,
    IMessagingEmailSettingsStore emailStore,
    ISecretStore secrets,
    IMailRelayClientService clients)
{
    private const string ClientName = "LMS server email";
    private const string ClientUsername = "lms-server-email";

    public async Task<MailRelaySetupPreview> PreviewAsync(string domainName, CancellationToken cancellationToken = default)
    {
        var dashboard = await gateway.GetDashboardAsync(cancellationToken);
        var zone = dashboard.Cloudflare.Domains.FirstOrDefault(item => item.DomainName.Equals(domainName, StringComparison.OrdinalIgnoreCase));
        if (zone is not { Paused: false, RelayConfigured: true, RelayUsesCloudflareTunnel: true, RelayOwnedByThisLms: true })
            throw new InvalidOperationException("Set up a relay owned by this LMS server for the selected zone first.");
        var configuration = await relayStore.GetConfigurationAsync(cancellationToken);
        var domain = (await relayStore.ListDomainsAsync(cancellationToken)).FirstOrDefault(item => item.DomainName.Equals(zone.DomainName, StringComparison.OrdinalIgnoreCase));
        var client = (await relayStore.ListClientsAsync(cancellationToken)).FirstOrDefault(item => item.Username == ClientUsername);
        if (client is not null && client.Name != ClientName)
            throw new InvalidOperationException($"The SMTP username {ClientUsername} is already in use. Manage it in Mail Relay first.");
        var request = new MailRelaySetupRequest(
            zone.ZoneId,
            configuration?.Enabled == true ? configuration.RelayHostname : $"smtp.{zone.DomainName}",
            zone.DomainName,
            domain?.CurrentDkimSelector ?? "lms",
            ClientName,
            ClientUsername,
            configuration?.AllowTailscale ?? false,
            configuration?.AllowTrustedLan ?? false)
        {
            ConfigureLmsEmail = true
        };
        return await relay.PreviewSetupAsync(request, cancellationToken);
    }

    public async Task<MailRelaySetupResult> ConfigureEmailAsync(
        MailRelaySetupRequest request,
        MailRelaySetupResult result,
        CancellationToken cancellationToken = default)
    {
        if (!request.ConfigureLmsEmail || !result.Success || !result.OpenRelayTestPassed ||
            request.ApplicationUsername != ClientUsername || result.Username != ClientUsername ||
            !result.SendingDomain.Equals(request.SendingDomain, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mail Relay must pass setup and its security checks before LMS email can be enabled.");
        var configuration = await relayStore.GetConfigurationAsync(cancellationToken);
        var domain = (await relayStore.ListDomainsAsync(cancellationToken)).FirstOrDefault(item =>
            item.Enabled && item.DomainName.Equals(request.SendingDomain, StringComparison.OrdinalIgnoreCase));
        var client = (await relayStore.ListClientsAsync(cancellationToken)).FirstOrDefault(item =>
            item.Enabled && item.Username == ClientUsername && item.Name == ClientName);
        if (configuration?.Enabled != true || domain is null || client is null ||
            !client.AllowedSenderDomains.Contains(domain.DomainName, StringComparer.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(configuration.TlsCertificateSecretReference))
            throw new InvalidOperationException("The managed relay, TLS certificate and LMS SMTP account must be ready before enabling email.");

        var settings = await emailStore.GetAsync(cancellationToken);
        var password = result.GeneratedPassword;
        var existingReference = settings.SmtpUsername == ClientUsername ? settings.SmtpPasswordSecretReference : null;
        if (string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(existingReference))
            password = await secrets.ResolveSecretAsync(existingReference, cancellationToken);
        var passwordReference = existingReference;
        if (string.IsNullOrEmpty(password))
        {
            // A failed earlier setup may have created the dedicated account without saving its password.
            password = clients.GeneratePassword();
            var allowedDomains = (await relayStore.ListDomainsAsync(cancellationToken))
                .Where(item => item.Enabled && client.AllowedSenderDomains.Contains(item.DomainName, StringComparer.OrdinalIgnoreCase))
                .Select(item => item.Id).ToArray();
            var saved = await clients.SaveAsync(new MailRelayClientSaveRequest(
                client.Id, ClientName, ClientUsername, password, allowedDomains), cancellationToken);
            if (!saved.Success) throw new InvalidOperationException(saved.Summary);
            passwordReference = null;
        }
        if (result.GeneratedPassword is not null) passwordReference = null;
        var createdSecret = string.IsNullOrEmpty(passwordReference);
        if (createdSecret)
            passwordReference = await secrets.StoreSecretAsync(password, "LMS server Mail Relay SMTP password", cancellationToken);
        try
        {
            await emailStore.SaveAsync(settings with
            {
                IsEnabled = true,
                Provider = MessagingEmailProvider.Smtp,
                SenderAddress = $"lms@{domain.DomainName}",
                SenderDisplayName = "Linux Made Sane",
                SmtpHost = "127.0.0.1",
                SmtpPort = configuration.SubmissionPort,
                SmtpUseStartTls = true,
                SmtpUsername = ClientUsername,
                SmtpPasswordSecretReference = passwordReference,
                LastVerifiedAtUtc = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            }, cancellationToken);
        }
        catch
        {
            if (createdSecret) await secrets.DeleteSecretAsync(passwordReference!, CancellationToken.None);
            throw;
        }
        var summary = $"LMS email is enabled as lms@{domain.DomainName} through the local Mail Relay.";
        return result with
        {
            GeneratedPassword = null,
            Summary = summary,
            Steps = [.. result.Steps, new("lms-email", "LMS email", MailRelaySetupStepState.Complete, summary)]
        };
    }
}
