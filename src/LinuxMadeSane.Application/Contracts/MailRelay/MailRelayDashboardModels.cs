// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.MailRelay;

namespace LinuxMadeSane.Application.Contracts.MailRelay;

public enum MailRelayPreflightCheckState
{
    NotRun = 0,
    Pass = 1,
    Warning = 2,
    Failed = 3,
    NotAvailable = 4
}

public static class MailRelayPreflightCheckKeys
{
    public const string EdgeGateway = "edge-gateway";
    public const string CloudflareAuthentication = "cloudflare-authentication";
    public const string CloudflareZone = "cloudflare-zone";
    public const string DnsList = "dns-list";
    public const string DnsEdit = "dns-edit";
    public const string PublicIpv4 = "public-ipv4";
    public const string OutboundSmtp = "outbound-smtp";
    public const string ReverseDns = "reverse-dns";
    public const string Docker = "docker";
    public const string Tailscale = "tailscale";
}

public sealed record MailRelayPreflightCheck(
    string Key,
    string Label,
    MailRelayPreflightCheckState State,
    string Value,
    string Detail);

public sealed record MailRelayCloudflareZoneOption(
    string ZoneId,
    string ZoneName,
    string Status,
    bool Paused,
    bool IsSavedDefault);

public sealed record MailRelayPreflightResult(
    string CloudflareZoneId,
    string CloudflareZoneName,
    IReadOnlyList<MailRelayCloudflareZoneOption> AvailableZones,
    string SuggestedRelayHostname,
    string PublicIpAddress,
    string ReverseDnsHostname,
    IReadOnlyList<MailRelayPreflightCheck> Checks,
    bool DnsEditWasTested,
    DateTimeOffset CheckedAtUtc)
{
    public MailRelayPreflightCheck GetCheck(string key) =>
        Checks.First(check => check.Key.Equals(key, StringComparison.Ordinal));

    public bool EdgeGatewayConfigured =>
        GetCheck(MailRelayPreflightCheckKeys.EdgeGateway).State == MailRelayPreflightCheckState.Pass &&
        GetCheck(MailRelayPreflightCheckKeys.CloudflareAuthentication).State == MailRelayPreflightCheckState.Pass &&
        GetCheck(MailRelayPreflightCheckKeys.CloudflareZone).State == MailRelayPreflightCheckState.Pass &&
        GetCheck(MailRelayPreflightCheckKeys.DnsList).State == MailRelayPreflightCheckState.Pass;

    public bool CloudflareZonesAvailable =>
        GetCheck(MailRelayPreflightCheckKeys.EdgeGateway).State == MailRelayPreflightCheckState.Pass &&
        GetCheck(MailRelayPreflightCheckKeys.CloudflareAuthentication).State == MailRelayPreflightCheckState.Pass &&
        AvailableZones.Count > 0;

    public bool CloudflareDnsReady =>
        GetCheck(MailRelayPreflightCheckKeys.CloudflareAuthentication).State == MailRelayPreflightCheckState.Pass &&
        GetCheck(MailRelayPreflightCheckKeys.CloudflareZone).State == MailRelayPreflightCheckState.Pass &&
        GetCheck(MailRelayPreflightCheckKeys.DnsList).State == MailRelayPreflightCheckState.Pass &&
        GetCheck(MailRelayPreflightCheckKeys.DnsEdit).State == MailRelayPreflightCheckState.Pass;

    public bool HostSuitable =>
        GetCheck(MailRelayPreflightCheckKeys.PublicIpv4).State == MailRelayPreflightCheckState.Pass &&
        GetCheck(MailRelayPreflightCheckKeys.OutboundSmtp).State == MailRelayPreflightCheckState.Pass &&
        DockerCanBePrepared;

    public bool DockerCanBePrepared
    {
        get
        {
            var docker = GetCheck(MailRelayPreflightCheckKeys.Docker);
            return docker.State == MailRelayPreflightCheckState.Pass ||
                   docker.State == MailRelayPreflightCheckState.Warning &&
                   docker.Value is "INSTALL REQUIRED" or "START REQUIRED";
        }
    }

    public bool CanConfigure => CloudflareDnsReady && HostSuitable;
}

public sealed record MailRelayDashboardViewModel(
    MailRelayOperationalStatus Status,
    string StatusSummary,
    MailRelayConfiguration? Configuration,
    IReadOnlyList<MailRelayDomain> Domains,
    IReadOnlyList<MailRelayClient> Clients,
    MailRelayPreflightResult Preflight);
