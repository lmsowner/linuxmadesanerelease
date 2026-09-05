// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.SystemInfo;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.MailRelay;

namespace LinuxMadeSane.Infrastructure.Services.ConfigurationSummary;

public sealed class MailRelayConfigurationSummaryProvider(IMailRelayStore store)
    : ILmsConfigurationSummaryProvider
{
    public string ModuleName => "Mail Relay";

    public int SortOrder => 100;

    public string NavigationUrl => "/mail-relay";

    public async Task<LmsConfigurationSummary> GetConfigurationSummaryAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await store.GetConfigurationAsync(cancellationToken);
        if (configuration is null)
        {
            return Empty(LmsConfigurationSummaryStatus.NotConfigured);
        }

        var domains = await store.ListDomainsAsync(cancellationToken);
        var clients = await store.ListClientsAsync(cancellationToken);
        var warnings = new List<string>();
        if (configuration.Enabled && domains.Count(domain => domain.Enabled) == 0)
        {
            warnings.Add("No sending domains are enabled.");
        }

        if (configuration.Enabled && clients.Count(client => client.Enabled) == 0 && !configuration.AllowLegacyPort25)
        {
            warnings.Add("No SMTP applications are enabled.");
        }

        var status = !configuration.Enabled
            ? LmsConfigurationSummaryStatus.Disabled
            : warnings.Count > 0
                ? LmsConfigurationSummaryStatus.Warning
                : LmsConfigurationSummaryStatus.Configured;
        var enabledDomains = domains.Where(domain => domain.Enabled).ToArray();
        var submissionModes = BuildSubmissionModes(configuration);
        var authentication = BuildAuthenticationSummary(enabledDomains);
        var dmarcPolicies = enabledDomains.Select(domain => SummaryValueFormatter.Title(domain.DmarcPolicy.ToString()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new LmsConfigurationSummary(
            ModuleName,
            status,
            "Outbound SMTP compatibility relay",
            [
                new("Relay", configuration.RelayHostname),
                new("Sending domains", enabledDomains.Length.ToString()),
                new("Applications", clients.Count(client => client.Enabled).ToString()),
                new("Delivery", SummaryValueFormatter.Words(configuration.DeliveryMode.ToString())),
                new("Submission", submissionModes.Length == 0 ? "Localhost only" : string.Join(" / ", submissionModes)),
                new("Authentication", authentication),
                new("DMARC policy", dmarcPolicies.Length == 0 ? "Not configured" : string.Join(" / ", dmarcPolicies))
            ],
            warnings,
            "/mail-relay",
            SortOrder);
    }

    private static string[] BuildSubmissionModes(MailRelayConfiguration configuration)
    {
        var modes = new List<string>();
        if (configuration.AllowTailscale) modes.Add("Tailscale");
        if (configuration.AllowTrustedLan) modes.Add("Trusted LAN");
        if (configuration.AllowPublicSubmission) modes.Add("Public");
        if (configuration.AllowLegacyPort25) modes.Add("Filtered port 25");
        return modes.ToArray();
    }

    private static string BuildAuthenticationSummary(IReadOnlyList<MailRelayDomain> domains)
    {
        if (domains.Count == 0)
        {
            return "Not configured";
        }

        var mechanisms = new List<string>();
        if (domains.Any(domain => !string.IsNullOrWhiteSpace(domain.SpfCloudflareRecordId))) mechanisms.Add("SPF");
        if (domains.Any(domain => !string.IsNullOrWhiteSpace(domain.CurrentDkimPrivateKeySecretReference))) mechanisms.Add("DKIM");
        if (domains.Any(domain => !string.IsNullOrWhiteSpace(domain.DmarcCloudflareRecordId))) mechanisms.Add("DMARC");
        return mechanisms.Count == 0 ? "Setup pending" : string.Join(" / ", mechanisms);
    }

    private LmsConfigurationSummary Empty(LmsConfigurationSummaryStatus status) =>
        new(ModuleName, status, "Outbound SMTP compatibility relay", [], [], "/mail-relay", SortOrder);
}
