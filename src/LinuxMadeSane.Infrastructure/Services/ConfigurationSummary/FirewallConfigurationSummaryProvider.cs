// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.SystemInfo;
using LinuxMadeSane.Application.Interfaces;

namespace LinuxMadeSane.Infrastructure.Services.ConfigurationSummary;

public sealed class FirewallConfigurationSummaryProvider(IFirewallManagementService firewallService)
    : ILmsConfigurationSummaryProvider
{
    public string ModuleName => "Firewall";

    public int SortOrder => 50;

    public string NavigationUrl => "/settings?tab=firewall";

    public async Task<LmsConfigurationSummary> GetConfigurationSummaryAsync(CancellationToken cancellationToken = default)
    {
        var status = await firewallService.GetStatusAsync(cancellationToken);
        if (!status.IsInstalled)
        {
            return Empty(LmsConfigurationSummaryStatus.NotConfigured);
        }

        var lmsManagedRules = status.Rules.Count(rule =>
            rule.Comment.StartsWith("LMS ", StringComparison.OrdinalIgnoreCase) ||
            rule.RawText.Contains("Linux Made Sane", StringComparison.OrdinalIgnoreCase));
        var summaryStatus = !status.IsActive
            ? LmsConfigurationSummaryStatus.Disabled
            : string.IsNullOrWhiteSpace(status.Warning)
                ? LmsConfigurationSummaryStatus.Configured
                : LmsConfigurationSummaryStatus.Warning;

        return new LmsConfigurationSummary(
            ModuleName,
            summaryStatus,
            "Local UFW access policy",
            [
                new("Status", status.IsActive ? "Enabled" : "Disabled"),
                new("Default incoming", SummaryValueFormatter.Title(status.IncomingPolicy)),
                new("Default outgoing", SummaryValueFormatter.Title(status.OutgoingPolicy)),
                new("Rules", status.Rules.Count.ToString()),
                new("LMS managed rules", lmsManagedRules.ToString()),
                new("Logging", SummaryValueFormatter.Title(status.Logging))
            ],
            string.IsNullOrWhiteSpace(status.Warning) ? [] : [status.Warning],
            NavigationUrl,
            SortOrder);
    }

    private LmsConfigurationSummary Empty(LmsConfigurationSummaryStatus status) =>
        new(ModuleName, status, "Local UFW access policy", [], [], NavigationUrl, SortOrder);
}
