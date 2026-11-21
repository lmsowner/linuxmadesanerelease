// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.Cloudflare;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Infrastructure.Services;

namespace LinuxMadeSane.Tests.Infrastructure;

public sealed class MailRelayPreflightServiceTests
{
    [Theory]
    [InlineData("ip=51.195.223.135", "51.195.223.135")]
    [InlineData("ip=100.80.52.95", "")]
    [InlineData("ip=172.17.0.1", "")]
    [InlineData("ip=2001:db8::1", "")]
    [InlineData("ip=0.0.0.0", "")]
    [InlineData("no address", "")]
    public async Task IPv4PreflightSelectsAnIPv4DestinationOnDualStackHosts(string ipv4Trace, string expected)
    {
        using var trace = new DualStackTrace(ipv4Trace);
        var detector = new MailRelayPreflightService(new StubExposureStore(null), new StubZoneService(),
            new RecordingDnsService(), new StubSecretStore(null), null!, trace);
        var result = await detector.DetectPublicIpv4Async(default);
        Assert.True(trace.UsedIpv4);
        Assert.Equal(expected, result.Address);
        Assert.Equal(expected.Length > 0, result.Success);
    }

    private sealed class DualStackTrace(string ipv4Trace) : HttpMessageHandler, IHttpClientFactory
    {
        public bool UsedIpv4 { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            UsedIpv4 = request.RequestUri!.HostNameType == UriHostNameType.IPv4;
            var body = UsedIpv4 ? ipv4Trace : "ip=2001:db8::1";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task InspectAsync_InitialReadIsReadOnlyAndReusesSavedEdgeGatewayConfiguration()
    {
        var now = DateTimeOffset.UtcNow;
        var cloudflare = new RecordingDnsService();
        var service = new MailRelayPreflightService(
            new StubExposureStore(new CloudflareSettings(
                AiLocalMachine.ManagedHostId, "account", "Account", "zone", "example.test",
                "secret://cloudflare", now, now)),
            new StubZoneService(
                new CloudflareZone("zone", "example.test", "account", "Account", "active", false),
                new CloudflareZone("zone-two", "second.test", "account", "Account", "active", false)),
            cloudflare,
            new StubSecretStore("existing-edge-token"),
            new RejectingCommandRunner(),
            new RejectingHttpClientFactory());

        var result = await service.InspectAsync(false);

        Assert.True(result.EdgeGatewayConfigured);
        Assert.Equal("example.test", result.CloudflareZoneName);
        Assert.Equal("smtp.example.test", result.SuggestedRelayHostname);
        Assert.Equal(2, result.AvailableZones.Count);
        Assert.Contains(result.AvailableZones, zone => zone.ZoneName == "second.test");
        Assert.Equal(MailRelayPreflightCheckState.NotRun,
            result.GetCheck(MailRelayPreflightCheckKeys.DnsEdit).State);
        var selected = await service.InspectAsync(false, "zone-two");
        Assert.Equal("zone-two", selected.CloudflareZoneId);
        Assert.Equal("second.test", selected.CloudflareZoneName);
        Assert.Equal("zone-two", cloudflare.LastListedZoneId);
        Assert.Equal(2, cloudflare.ListCalls);
        Assert.Equal(0, cloudflare.CreateCalls);
        Assert.Equal(0, cloudflare.DeleteCalls);
    }

    [Fact]
    public async Task InspectAsync_MissingEdgeGatewayLocksAllMailRelayPreflightChecks()
    {
        var cloudflare = new RecordingDnsService();
        var service = new MailRelayPreflightService(
            new StubExposureStore(null),
            new StubZoneService(),
            cloudflare,
            new StubSecretStore(null),
            new RejectingCommandRunner(),
            new RejectingHttpClientFactory());

        var result = await service.InspectAsync(true);

        Assert.False(result.EdgeGatewayConfigured);
        Assert.All(result.Checks.Skip(1), check => Assert.Equal(MailRelayPreflightCheckState.NotAvailable, check.State));
        Assert.Equal(0, cloudflare.ListCalls);
        Assert.Equal(0, cloudflare.CreateCalls);
    }

    [Fact]
    public async Task InspectAsync_ReturnsEveryCloudflareZoneInsteadOfOnlyTheAlphabeticalFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var zones = Enumerable.Range(1, 25)
            .Select(index => new CloudflareZone(
                $"zone-{index:00}",
                $"domain-{index:00}.test",
                "account",
                "Account",
                "active",
                false))
            .ToArray();
        var service = new MailRelayPreflightService(
            new StubExposureStore(new CloudflareSettings(
                AiLocalMachine.ManagedHostId, "account", "Account", zones[0].Id, zones[0].Name,
                "secret://cloudflare", now, now)),
            new StubZoneService(zones),
            new RecordingDnsService(),
            new StubSecretStore("existing-edge-token"),
            new RejectingCommandRunner(),
            new RejectingHttpClientFactory());

        var result = await service.InspectAsync(false, zones[^1].Id);

        Assert.Equal(25, result.AvailableZones.Count);
        Assert.Equal(zones[^1].Id, result.CloudflareZoneId);
        Assert.Equal(zones[^1].Name, result.CloudflareZoneName);
    }

    [Fact]
    public async Task VerifyDnsEditAsync_DoesNotReportMissingPermissionAfterRecordCreationSucceeded()
    {
        var cloudflare = new RecordingDnsService { DeleteFailuresRemaining = 2 };
        var service = new MailRelayPreflightService(
            new StubExposureStore(null),
            new StubZoneService(),
            cloudflare,
            new StubSecretStore(null),
            new RejectingCommandRunner(),
            new RejectingHttpClientFactory());

        var check = await service.VerifyDnsEditAsync("token", "zone", "example.test", CancellationToken.None);

        Assert.Equal(MailRelayPreflightCheckState.Pass, check.State);
        Assert.Equal("PASS", check.Value);
        Assert.Contains("DNS Edit was verified", check.Detail, StringComparison.Ordinal);
        Assert.Equal(1, cloudflare.CreateCalls);
        Assert.Equal(2, cloudflare.DeleteCalls);
    }

    [Fact]
    public async Task InspectDockerAsync_OffersManagedInstallationWhenDockerIsMissing()
    {
        var runner = new ScriptedCommandRunner(
            Result(127, standardError: "docker: command not found"),
            Result(0, "apt 2.8"));
        var service = CreateForDockerInspection(runner);

        var check = await service.InspectDockerAsync(CancellationToken.None);

        Assert.Equal(MailRelayPreflightCheckState.Warning, check.State);
        Assert.Equal("INSTALL REQUIRED", check.Value);
        Assert.Contains("can install", check.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Collection(
            runner.Requests,
            request =>
            {
                Assert.Equal("docker", request.FileName);
                Assert.False(request.RequiresSudo);
            },
            request => Assert.Equal("apt-get", request.FileName));
    }

    [Fact]
    public async Task InspectDockerAsync_UsesLmsPrivilegeBoundaryWhenSocketIsRestricted()
    {
        var runner = new ScriptedCommandRunner(
            Result(0, "Docker version 27.5.1"),
            Result(1, standardError: "permission denied while trying to connect to the Docker daemon socket"),
            Result(0, "27.5.1"));
        var service = CreateForDockerInspection(runner);

        var check = await service.InspectDockerAsync(CancellationToken.None);

        Assert.Equal(MailRelayPreflightCheckState.Pass, check.State);
        Assert.Equal("PASS", check.Value);
        Assert.Contains("privileged runner", check.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(runner.Requests[1].RequiresSudo);
        Assert.True(runner.Requests[2].RequiresSudo);
    }

    private static MailRelayPreflightService CreateForDockerInspection(ILinuxCommandRunner commandRunner) =>
        new(
            new StubExposureStore(null),
            new StubZoneService(),
            new RecordingDnsService(),
            new StubSecretStore(null),
            commandRunner,
            new RejectingHttpClientFactory());

    private static LinuxCommandResult Result(int exitCode, string standardOutput = "", string standardError = "") =>
        new(string.Empty, exitCode, standardOutput, standardError, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);

    private sealed class StubZoneService(params CloudflareZone[] zones) : ICloudflareZoneService
    {
        public Task<IReadOnlyList<CloudflareAccount>> ListAccountsAsync(
            string apiToken, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudflareAccount>>([]);

        public Task<IReadOnlyList<CloudflareZone>> ListZonesAsync(
            string apiToken, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudflareZone>>(zones);
    }

    private sealed class RecordingDnsService : ICloudflareDnsService
    {
        public int ListCalls { get; private set; }
        public int CreateCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public string LastListedZoneId { get; private set; } = string.Empty;
        public int DeleteFailuresRemaining { get; init; }

        public Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(
            string apiToken, string zoneId, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            LastListedZoneId = zoneId;
            return Task.FromResult<IReadOnlyList<CloudflareDnsRecord>>([]);
        }

        public Task<CloudflareDnsRecord> CreateRecordAsync(
            string apiToken, string zoneId, CloudflareDnsRecord record, CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            return Task.FromResult(record with { Id = "created" });
        }

        public Task<CloudflareDnsRecord> UpdateRecordAsync(
            string apiToken, string zoneId, CloudflareDnsRecord record, CancellationToken cancellationToken = default) =>
            Task.FromResult(record);

        public Task DeleteRecordAsync(
            string apiToken, string zoneId, string recordId, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            if (DeleteFailuresRemaining >= DeleteCalls)
            {
                throw new CloudflareApiException(
                    500,
                    [new CloudflareApiError(1000, "Temporary Cloudflare cleanup failure.", null, null)]);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class StubExposureStore(CloudflareSettings? settings) : ICloudflareExposureStore
    {
        public Task<CloudflareSettings?> GetSettingsAsync(Guid managedHostId, CancellationToken cancellationToken = default) =>
            Task.FromResult(settings);

        public Task SaveSettingsAsync(CloudflareSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteSettingsAsync(Guid managedHostId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ExposedServiceConfig>> ListConfigsAsync(Guid managedHostId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExposedServiceConfig>>([]);
        public Task<ExposedServiceConfig?> GetConfigAsync(Guid configId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ExposedServiceConfig?>(null);
        public Task<ExposedServiceConfig?> GetConfigByHostnameAsync(Guid managedHostId, string hostname, CancellationToken cancellationToken = default) =>
            Task.FromResult<ExposedServiceConfig?>(null);
        public Task SaveConfigAsync(ExposedServiceConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteConfigAsync(Guid configId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubSecretStore(string? token) : ISecretStore
    {
        public Task<string?> ResolveSecretAsync(string secretReference, CancellationToken cancellationToken = default) => Task.FromResult(token);
        public Task<SecretReferenceMetadata?> GetMetadataAsync(string secretReference, CancellationToken cancellationToken = default) =>
            Task.FromResult<SecretReferenceMetadata?>(null);
        public Task<string> StoreSecretAsync(string secretValue, string purpose, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Preflight must not store the Edge Gateway token.");
        public Task DeleteSecretAsync(string secretReference, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Preflight must not delete the Edge Gateway token.");
    }

    private sealed class RejectingCommandRunner : ILinuxCommandRunner
    {
        public Task<LinuxCommandResult> RunAsync(
            LinuxCommandRequest request, bool dryRun, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Host probes must not run during initial inspection.");
    }

    private sealed class ScriptedCommandRunner(params LinuxCommandResult[] results) : ILinuxCommandRunner
    {
        private readonly Queue<LinuxCommandResult> remaining = new(results);
        public List<LinuxCommandRequest> Requests { get; } = [];

        public Task<LinuxCommandResult> RunAsync(
            LinuxCommandRequest request,
            bool dryRun,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(remaining.Dequeue());
        }
    }

    private sealed class RejectingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("Public IP detection must not run during initial inspection.");
    }
}
