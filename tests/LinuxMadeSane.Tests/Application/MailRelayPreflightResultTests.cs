// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.MailRelay;

namespace LinuxMadeSane.Tests.Application;

public sealed class MailRelayPreflightResultTests
{
    [Fact]
    public void CanConfigure_RequiresWorkingEdgeDnsEditAndHostTransport_ButNotPtr()
    {
        var result = BuildResult(
            MailRelayPreflightCheckState.Pass,
            MailRelayPreflightCheckState.Pass,
            MailRelayPreflightCheckState.Warning);

        Assert.True(result.EdgeGatewayConfigured);
        Assert.True(result.CloudflareDnsReady);
        Assert.True(result.HostSuitable);
        Assert.True(result.CanConfigure);
    }

    [Fact]
    public void CanConfigure_IsFalseWhenCloudflareDnsEditIsUnavailable()
    {
        var result = BuildResult(
            MailRelayPreflightCheckState.Failed,
            MailRelayPreflightCheckState.Pass,
            MailRelayPreflightCheckState.Pass);

        Assert.True(result.EdgeGatewayConfigured);
        Assert.False(result.CloudflareDnsReady);
        Assert.False(result.CanConfigure);
    }

    [Fact]
    public void CanConfigure_AllowsDockerToBeInstalledDuringApprovedSetup()
    {
        var result = BuildResult(
            MailRelayPreflightCheckState.Pass,
            MailRelayPreflightCheckState.Pass,
            MailRelayPreflightCheckState.Warning,
            new MailRelayPreflightCheck(
                MailRelayPreflightCheckKeys.Docker,
                "Docker",
                MailRelayPreflightCheckState.Warning,
                "INSTALL REQUIRED",
                "Docker will be installed during setup."));

        Assert.True(result.DockerCanBePrepared);
        Assert.True(result.HostSuitable);
        Assert.True(result.CanConfigure);
    }

    private static MailRelayPreflightResult BuildResult(
        MailRelayPreflightCheckState dnsEdit,
        MailRelayPreflightCheckState outboundSmtp,
        MailRelayPreflightCheckState ptr,
        MailRelayPreflightCheck? docker = null) =>
        new(
            "zone-id",
            "example.test",
            [new MailRelayCloudflareZoneOption("zone-id", "example.test", "active", false, true)],
            "smtp.example.test",
            "203.0.113.10",
            ptr == MailRelayPreflightCheckState.Pass ? "smtp.example.test" : string.Empty,
            [
                Check(MailRelayPreflightCheckKeys.EdgeGateway, MailRelayPreflightCheckState.Pass),
                Check(MailRelayPreflightCheckKeys.CloudflareAuthentication, MailRelayPreflightCheckState.Pass),
                Check(MailRelayPreflightCheckKeys.CloudflareZone, MailRelayPreflightCheckState.Pass),
                Check(MailRelayPreflightCheckKeys.DnsList, MailRelayPreflightCheckState.Pass),
                Check(MailRelayPreflightCheckKeys.DnsEdit, dnsEdit),
                Check(MailRelayPreflightCheckKeys.PublicIpv4, MailRelayPreflightCheckState.Pass),
                Check(MailRelayPreflightCheckKeys.OutboundSmtp, outboundSmtp),
                Check(MailRelayPreflightCheckKeys.ReverseDns, ptr),
                docker ?? Check(MailRelayPreflightCheckKeys.Docker, MailRelayPreflightCheckState.Pass),
                Check(MailRelayPreflightCheckKeys.Tailscale, MailRelayPreflightCheckState.Warning)
            ],
            true,
            DateTimeOffset.UtcNow);

    private static MailRelayPreflightCheck Check(string key, MailRelayPreflightCheckState state) =>
        new(key, key, state, state.ToString(), string.Empty);
}
