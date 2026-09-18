// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Cloudflare;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalHttpServiceDiscoveryService : ILocalHttpServiceDiscoveryService
{
    private const int MaxTailnetPeersToProbe = 96;
    private const int MaxDockerPublishedPortsToProbe = 128;
    private const int MaxFaviconBytes = 32_768;
    private const int MaxConcurrentHostChecks = 32;
    private const int MaxConcurrentTcpLivenessChecks = 256;
    private const int MaxConcurrentProbes = 64;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1400);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan LanConnectTimeout = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan ReverseLookupTimeout = TimeSpan.FromMilliseconds(280);
    private static readonly int[] CommonHomelabPorts =
    [
        80, 81, 443, 1880, 1984, 2283, 3000, 3001, 5000, 5001, 5380, 5601,
        6767, 6789, 7125, 7443, 7745, 7878, 8000, 8006, 8043, 8080, 8083,
        8096, 8111, 8112, 8123, 8200, 8384, 8443, 8686, 8787, 8920, 8971,
        8989, 9000, 9001, 9090, 9091, 9443, 9696, 10000, 10443, 11443, 15672,
        18080, 19999, 32400
    ];
    private static readonly HashSet<int> CommonHomelabPortSet = new(CommonHomelabPorts);
    private static readonly int[] ExpandedPorts =
    [
        82, 88, 800, 808, 8008, 8009, 2342, 5080, 7000, 7126, 8081, 8888, 11434, 50000, 50001
    ];
    private static readonly int[] HostLivenessPorts =
    [
        80, 443, 8080, 8123, 8443, 9443, 5000, 5001, 3000, 8006, 8096, 9000, 9090, 10000, 32400, 22, 53, 139, 445
    ];
    private static readonly HashSet<int> HostLivenessPortSet = new(HostLivenessPorts);
    private static readonly HashSet<int> HttpsPreferredPorts =
    [
        443, 5001, 7443, 8043, 8443, 8920, 9443, 10443
    ];
    private static readonly int[] HttpsConventionPorts = BuildHttpsConventionPorts();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly Regex TitleRegex = new(
        "<title[^>]*>\\s*(?<title>.*?)\\s*</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex FaviconLinkTagRegex = new(
        "<link[^>]+rel\\s*=\\s*[\\\"']?(?:shortcut\\s+)?(?:apple-touch-)?icon[\\\"']?[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex FaviconHrefRegex = new(
        "href\\s*=\\s*[\\\"'](?<href>[^\\\"']+)[\\\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpServiceDiscoveryStorageSettings storageSettings;
    private readonly ILinuxCommandRunner commandRunner;
    private readonly TailscalePeerStatusReader tailscalePeerStatusReader;

    public LocalHttpServiceDiscoveryService(
        ILinuxCommandRunner commandRunner,
        HttpServiceDiscoveryStorageSettings storageSettings)
        : this(commandRunner, storageSettings, new TailscalePeerStatusReader(commandRunner))
    {
    }

    internal LocalHttpServiceDiscoveryService(
        ILinuxCommandRunner commandRunner,
        HttpServiceDiscoveryStorageSettings storageSettings,
        TailscalePeerStatusReader tailscalePeerStatusReader)
    {
        this.commandRunner = commandRunner;
        this.storageSettings = storageSettings;
        this.tailscalePeerStatusReader = tailscalePeerStatusReader;
    }

    public async Task<IReadOnlyList<LocalHttpServiceEndpoint>> GetCachedAsync(CancellationToken cancellationToken = default) =>
        SortEndpoints(await ReadCacheAsync(cancellationToken));

    public Task<IReadOnlyList<LocalHttpServiceEndpoint>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        DiscoverAsync(new LocalHttpServiceDiscoveryRequest(), cancellationToken);

    public async Task<IReadOnlyList<LocalHttpServiceEndpoint>> DiscoverAsync(
        LocalHttpServiceDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        return await DiscoverAsync(request, progress: null, cancellationToken);
    }

    public async Task<IReadOnlyList<LocalHttpServiceEndpoint>> DiscoverAsync(
        LocalHttpServiceDiscoveryRequest request,
        IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
        CancellationToken cancellationToken = default)
    {
        var requestedScopes = BuildRequestedScopes(request);
        if (requestedScopes.Count == 0)
        {
            return await GetCachedAsync(cancellationToken);
        }

        progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
            "Preparing HTTP/S service scan...",
            0,
            0,
            0));

        var hosts = new List<HttpProbeHost>();
        if (request.IncludeLocalhost)
        {
            hosts.AddRange(BuildLocalhostProbeHosts());
        }

        if (request.IncludeDocker)
        {
            hosts.AddRange(await BuildDockerProbeHostsAsync(cancellationToken));
        }

        if (request.IncludeLan)
        {
            hosts.AddRange(await BuildLanProbeHostsAsync(cancellationToken));
        }

        if (request.IncludeTailnet)
        {
            hosts.AddRange(await BuildTailnetProbeHostsAsync(cancellationToken));
        }

        var totalProbeCount = hosts.Count;
        progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
            totalProbeCount == 0
                ? "No HTTP/S scan targets were available."
                : $"Checking {totalProbeCount} local discovery target(s) for live HTTP/S services...",
            0,
            totalProbeCount,
            0));

        var discovered = await ProbeHostsAsync(hosts, progress, cancellationToken);
        var existing = await ReadCacheAsync(cancellationToken);
        var merged = existing
            .Where(endpoint => !requestedScopes.Contains(endpoint.Scope))
            .Concat(discovered)
            .DistinctBy(endpoint => BuildEndpointKey(endpoint), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await WriteCacheAsync(merged, cancellationToken);
        var sorted = SortEndpoints(merged);
        progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
            discovered.Count == 0
                ? "Scan completed. No new HTTP/S services responded."
                : $"Scan completed. Found {discovered.Count} HTTP/S service(s).",
            totalProbeCount,
            totalProbeCount,
            discovered.Count,
            IsCompleted: true));
        return sorted;
    }

    private async Task<IReadOnlyList<LocalHttpServiceEndpoint>> ProbeHostsAsync(
        IReadOnlyList<HttpProbeHost> hosts,
        IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        if (hosts.Count == 0)
        {
            return [];
        }

        var progressState = new HttpDiscoveryProgressState(hosts.Count);
        var results = new ConcurrentDictionary<string, LocalHttpServiceEndpoint>(StringComparer.OrdinalIgnoreCase);
        using var hostConcurrency = new SemaphoreSlim(MaxConcurrentHostChecks);
        using var tcpConcurrency = new SemaphoreSlim(MaxConcurrentTcpLivenessChecks);
        using var serviceConcurrency = new SemaphoreSlim(MaxConcurrentProbes);
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true
        };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var tasks = hosts.Select(host => ProbeHostAsync(
                client,
                hostConcurrency,
                tcpConcurrency,
                serviceConcurrency,
                results,
                host,
                progress,
                progressState,
                cancellationToken))
            .ToArray();

        await Task.WhenAll(tasks);
        return results.Values.ToArray();
    }

    private static async Task ProbeHostAsync(
        HttpClient client,
        SemaphoreSlim hostConcurrency,
        SemaphoreSlim tcpConcurrency,
        SemaphoreSlim serviceConcurrency,
        ConcurrentDictionary<string, LocalHttpServiceEndpoint> results,
        HttpProbeHost host,
        IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
        HttpDiscoveryProgressState progressState,
        CancellationToken cancellationToken)
    {
        await hostConcurrency.WaitAsync(cancellationToken);
        IReadOnlyList<int> openPorts = [];
        try
        {
            if (host.IsLocalhostProbe || host.ProbeAddress is null)
            {
                openPorts = host.Ports;
            }
            else if (host.IsKnownLive || await IsLanHostReachableAsync(host.ProbeAddress, host.Ports, tcpConcurrency, cancellationToken))
            {
                openPorts = await FindOpenTcpPortsAsync(host.ProbeAddress, host.Ports, tcpConcurrency, cancellationToken);
            }

            var checkedCount = progressState.IncrementProbedCount();
            progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                $"Checked {checkedCount} of {progressState.TotalProbeCount} host(s); {openPorts.Count} open HTTP/S candidate port(s) at {host.ProbeHost}.",
                checkedCount,
                progressState.TotalProbeCount,
                progressState.FoundCount));
        }
        finally
        {
            hostConcurrency.Release();
        }

        if (openPorts.Count == 0)
        {
            return;
        }

        var priorityPorts = openPorts.Where(CommonHomelabPortSet.Contains).ToArray();
        var remainingPorts = openPorts.Where(port => !CommonHomelabPortSet.Contains(port)).ToArray();
        await ProbePortSetAsync(client, serviceConcurrency, results, host, priorityPorts, progress, progressState, cancellationToken);
        await ProbePortSetAsync(client, serviceConcurrency, results, host, remainingPorts, progress, progressState, cancellationToken);
    }

    private static async Task ProbePortSetAsync(
        HttpClient client,
        SemaphoreSlim serviceConcurrency,
        ConcurrentDictionary<string, LocalHttpServiceEndpoint> results,
        HttpProbeHost host,
        IReadOnlyList<int> ports,
        IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
        HttpDiscoveryProgressState progressState,
        CancellationToken cancellationToken)
    {
        await Task.WhenAll(ports.Select(port => ProbePortAsync(
            client,
            serviceConcurrency,
            results,
            host,
            port,
            progress,
            progressState,
            cancellationToken)));
    }

    private static async Task ProbePortAsync(
        HttpClient client,
        SemaphoreSlim serviceConcurrency,
        ConcurrentDictionary<string, LocalHttpServiceEndpoint> results,
        HttpProbeHost host,
        int port,
        IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
        HttpDiscoveryProgressState progressState,
        CancellationToken cancellationToken)
    {
        await serviceConcurrency.WaitAsync(cancellationToken);
        try
        {
            foreach (var scheme in GuessSchemes(port))
            {
                var endpoint = await TryProbeAsync(client, host, scheme, port, cancellationToken);
                if (endpoint is null || !results.TryAdd(BuildEndpointKey(endpoint), endpoint))
                {
                    continue;
                }

                var foundCount = progressState.IncrementFoundCount();
                progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                    $"Found {FormatEndpointForProgress(endpoint)}",
                    progressState.ProbedCount,
                    progressState.TotalProbeCount,
                    foundCount,
                    endpoint));
            }
        }
        finally
        {
            serviceConcurrency.Release();
        }
    }

    private static async Task<bool> IsLanHostReachableAsync(
        IPAddress address,
        IReadOnlyList<int> candidatePorts,
        SemaphoreSlim tcpConcurrency,
        CancellationToken cancellationToken)
    {
        if (await CanPingAsync(address, cancellationToken))
        {
            return true;
        }

        var commonPorts = candidatePorts.Where(HostLivenessPortSet.Contains).ToArray();
        if (commonPorts.Length > 0 && await CanOpenAnyTcpAsync(address, commonPorts, tcpConcurrency, cancellationToken, LanConnectTimeout))
        {
            return true;
        }

        var remainingPorts = candidatePorts.Where(port => !HostLivenessPortSet.Contains(port)).ToArray();
        return remainingPorts.Length > 0 &&
               await CanOpenAnyTcpAsync(address, remainingPorts, tcpConcurrency, cancellationToken, LanConnectTimeout);
    }

    private static async Task<bool> CanPingAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, 180);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> CanOpenAnyTcpAsync(
        IPAddress address,
        IReadOnlyList<int> ports,
        SemaphoreSlim tcpConcurrency,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        using var found = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, found.Token);
        var tasks = ports.Select(async port =>
        {
            if (await CanOpenTcpAsync(address, port, tcpConcurrency, linked.Token, timeout))
            {
                found.Cancel();
                return true;
            }

            return false;
        }).ToArray();

        try
        {
            return (await Task.WhenAll(tasks)).Any(value => value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return true;
        }
    }

    private static async Task<bool> CanOpenTcpAsync(
        IPAddress address,
        int port,
        SemaphoreSlim tcpConcurrency,
        CancellationToken cancellationToken,
        TimeSpan connectTimeout)
    {
        await tcpConcurrency.WaitAsync(cancellationToken);
        using var client = new TcpClient(address.AddressFamily);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(connectTimeout);

        try
        {
            await client.ConnectAsync(address, port, timeoutSource.Token);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            tcpConcurrency.Release();
        }
    }

    private static async Task<IReadOnlyList<int>> FindOpenTcpPortsAsync(
        IPAddress address,
        IReadOnlyList<int> ports,
        SemaphoreSlim tcpConcurrency,
        CancellationToken cancellationToken)
    {
        var open = new ConcurrentBag<int>();
        await Task.WhenAll(ports.Distinct().Select(async port =>
        {
            if (await CanOpenTcpAsync(address, port, tcpConcurrency, cancellationToken, ConnectTimeout))
            {
                open.Add(port);
            }
        }));

        return ports.Where(open.Contains).Distinct().ToArray();
    }

    private static async Task<LocalHttpServiceEndpoint?> TryProbeAsync(
        HttpClient client,
        HttpProbeHost host,
        string scheme,
        int port,
        CancellationToken cancellationToken)
    {
        var probeUrl = BuildUrl(scheme, host.ProbeHost, port);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{probeUrl}/");
            request.Headers.UserAgent.ParseAdd("LinuxMadeSane-http-service-discovery");

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            var page = await TryReadPageMetadataAsync(response, timeout.Token);
            var title = page.Title;
            var faviconDataUrl = await TryReadFaviconAsync(client, probeUrl, page.Sample, timeout.Token);
            var targetHost = host.TargetHost;
            if (host.ResolveHostName && host.ProbeAddress is not null)
            {
                targetHost = await TryResolveHostNameAsync(host.ProbeAddress, timeout.Token) ?? host.TargetHost;
            }

            return new LocalHttpServiceEndpoint(
                BuildUrl(scheme, targetHost, port),
                scheme,
                targetHost,
                port,
                (int)response.StatusCode,
                title,
                response.Headers.Server.ToString(),
                host.Scope,
                host.IpAddress,
                host.DisplayName,
                DateTimeOffset.UtcNow,
                faviconDataUrl,
                Confidence: BuildPresentationConfidence(title, faviconDataUrl, (int)response.StatusCode),
                ServiceName: BuildPresentationName(title, host.DisplayName, targetHost),
                ServiceKind: "unknown",
                Exposure: DiscoveryExposure.RequiresManualConfirmation,
                Fingerprint: $"http:{scheme}:{targetHost}:{port}",
                Evidence: BuildPresentationEvidence(title, response.Headers.Server.ToString(), host.Scope));
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<HttpProbeHost> BuildLocalhostProbeHosts()
    {
        var ports = GetLocalListeningPorts();
        return ports.Count == 0
            ? []
            :
            [
                new HttpProbeHost(
                    "localhost",
                    IPAddress.Loopback,
                    "localhost",
                    "Localhost",
                    "127.0.0.1",
                    null,
                    ports,
                    IsLocalhostProbe: true,
                    IsKnownLive: true,
                    ResolveHostName: false)
            ];
    }

    private async Task<IReadOnlyList<HttpProbeHost>> BuildLanProbeHostsAsync(CancellationToken cancellationToken)
    {
        var plans = BuildLanScanPlans();
        if (plans.Count == 0)
        {
            return [];
        }

        var neighbours = await LoadLanNeighbourEntriesAsync(cancellationToken);
        var advertisements = await LocalNetworkServiceAdvertisementDiscovery.DiscoverAsync(cancellationToken);
        var candidates = new List<(IPAddress Address, bool IsNeighbour)>();

        foreach (var plan in plans)
        {
            var neighbourAddresses = neighbours
                .Where(entry =>
                    entry.InterfaceName.Equals(plan.InterfaceName, StringComparison.OrdinalIgnoreCase) &&
                    IsAddressInSubnet(entry.Address, plan.LocalAddress, plan.PrefixLength))
                .Select(entry => entry.Address)
                .Distinct(IPAddressComparer.Instance)
                .OrderBy(AddressToUInt32)
                .ToArray();

            foreach (var address in neighbourAddresses)
            {
                candidates.Add((address, true));
            }

            var subnetAddresses = SshHostDiscoveryService.BuildLanCandidateAddresses(plan.LocalAddress, plan.PrefixLength)
                .OrderBy(AddressToUInt32)
                .ToArray();

            foreach (var address in subnetAddresses)
            {
                candidates.Add((address, false));
            }
        }

        var subnetHosts = candidates
            .GroupBy(candidate => candidate.Address.ToString(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Any(candidate => candidate.IsNeighbour))
            .ThenBy(group => AddressToUInt32(IPAddress.Parse(group.Key)))
            .Select(group =>
            {
                var item = group.First();
                var ipAddress = item.Address.ToString();
                return new HttpProbeHost(
                    ipAddress,
                    item.Address,
                    ipAddress,
                    "LAN",
                    ipAddress,
                    null,
                    BuildBaseScanPorts(),
                    IsLocalhostProbe: false,
                    IsKnownLive: group.Any(candidate => candidate.IsNeighbour),
                    ResolveHostName: true);
            })
            .ToArray();

        var advertisedHosts = advertisements
            .Select(advertisement => new HttpProbeHost(
                advertisement.Host,
                advertisement.Address,
                advertisement.Host,
                advertisement.Scope,
                advertisement.Address?.ToString(),
                advertisement.DisplayName,
                BuildBaseScanPorts().Concat([advertisement.Port]).Distinct().ToArray(),
                IsLocalhostProbe: false,
                IsKnownLive: true,
                ResolveHostName: false))
            .ToArray();

        return subnetHosts
            .Concat(advertisedHosts)
            .GroupBy(host => host.ProbeHost, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group
                    .OrderByDescending(host => !string.IsNullOrWhiteSpace(host.DisplayName))
                    .ThenBy(host => host.Scope.Equals("LAN", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .First();
                return first with
                {
                    Ports = group.SelectMany(host => host.Ports).Distinct().Order().ToArray(),
                    DisplayName = group.Select(host => host.DisplayName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? first.DisplayName,
                    IsKnownLive = group.Any(host => host.IsKnownLive)
                };
            })
            .ToArray();
    }

    private async Task<IReadOnlyList<HttpProbeHost>> BuildTailnetProbeHostsAsync(CancellationToken cancellationToken)
    {
        var status = await tailscalePeerStatusReader.ReadAsync(cancellationToken);
        if (!status.IsReady)
        {
            return [];
        }

        return status.Peers
            .Where(peer => (peer.Online || peer.Active) && IPAddress.TryParse(peer.IpAddress, out _))
            .Take(MaxTailnetPeersToProbe)
            .Select(peer =>
            {
                var address = IPAddress.Parse(peer.IpAddress);
                return new HttpProbeHost(
                    peer.Target,
                    address,
                    address.ToString(),
                    "Tailnet",
                    peer.IpAddress,
                    peer.DisplayName,
                    BuildBaseScanPorts(),
                    IsLocalhostProbe: false,
                    IsKnownLive: true,
                    ResolveHostName: false);
            })
            .ToArray();
    }

    private async Task<IReadOnlyList<HttpProbeHost>> BuildDockerProbeHostsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunDockerContainerListAsync(requiresSudo: false, cancellationToken);
            if (result.ExitCode != 0 && IsDockerSocketPermissionDenied(result))
            {
                result = await RunDockerContainerListAsync(requiresSudo: true, cancellationToken);
            }

            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return [];
            }

            return ParseDockerPublishedPortHosts(result.StandardOutput);
        }
        catch
        {
            return [];
        }
    }

    private async Task<LinuxCommandResult> RunDockerContainerListAsync(
        bool requiresSudo,
        CancellationToken cancellationToken) =>
        await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "docker",
                ["ps", "--format", "{{json .}}"],
                requiresSudo,
                TimeSpan.FromSeconds(5),
                "Inspect Docker containers with published HTTP/S ports")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

    private static bool IsDockerSocketPermissionDenied(LinuxCommandResult result)
    {
        var output = $"{result.StandardError}\n{result.StandardOutput}";
        return output.Contains("/var/run/docker.sock", StringComparison.OrdinalIgnoreCase) &&
               output.Contains("permission denied", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<HttpProbeHost> ParseDockerPublishedPortHosts(string output)
    {
        var publishedPorts = new List<DockerPublishedPort>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var containerName = GetJsonString(root, "Names") ?? GetJsonString(root, "Name");
                var image = GetJsonString(root, "Image");
                var portsText = GetJsonString(root, "Ports");
                if (string.IsNullOrWhiteSpace(containerName) || string.IsNullOrWhiteSpace(portsText))
                {
                    continue;
                }

                foreach (var publishedPort in ParseDockerPublishedPorts(containerName, image, portsText))
                {
                    publishedPorts.Add(publishedPort);
                }
            }
            catch
            {
            }
        }

        return publishedPorts
            .DistinctBy(port => $"{port.HostAddress}|{port.HostPort}|{port.ContainerName}|{port.ContainerPort}", StringComparer.OrdinalIgnoreCase)
            .Take(MaxDockerPublishedPortsToProbe)
            .GroupBy(port => BuildDockerProbeGroupKey(port), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var item = group.First();
                var bind = NormalizeDockerBindAddress(item.HostAddress);
                var targetHost = ResolveDockerTargetHost(bind);
                var probeAddress = ResolveDockerProbeAddress(bind);
                var ipAddress = ResolveDockerDisplayAddress(bind);
                var displayName = BuildDockerDisplayName(item);

                return new HttpProbeHost(
                    targetHost,
                    probeAddress,
                    targetHost,
                    "Docker",
                    ipAddress,
                    displayName,
                    group.Select(port => port.HostPort).Distinct().Order().ToArray(),
                    IsLocalhostProbe: true,
                    IsKnownLive: true,
                    ResolveHostName: false);
            })
            .ToArray();
    }

    private static IEnumerable<DockerPublishedPort> ParseDockerPublishedPorts(string containerName, string? image, string portsText)
    {
        foreach (var segment in portsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!segment.Contains("->", StringComparison.Ordinal) ||
                !segment.EndsWith("/tcp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = segment.Split("->", 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                continue;
            }

            var hostSide = parts[0].Trim();
            var containerSide = parts[1].Trim();
            var slashIndex = containerSide.IndexOf('/');
            if (slashIndex > 0)
            {
                containerSide = containerSide[..slashIndex];
            }

            var (hostAddress, hostPorts) = ParseDockerHostSide(hostSide);
            var containerPort = ParseFirstPort(containerSide);
            if (hostPorts.Count == 0 || containerPort <= 0)
            {
                continue;
            }

            foreach (var hostPort in hostPorts)
            {
                yield return new DockerPublishedPort(
                    containerName,
                    image,
                    hostAddress,
                    hostPort,
                    containerPort);
            }
        }
    }

    private static (string HostAddress, IReadOnlyList<int> HostPorts) ParseDockerHostSide(string hostSide)
    {
        var normalized = hostSide.Trim();
        var separatorIndex = normalized.LastIndexOf(':');
        if (separatorIndex < 0)
        {
            return (string.Empty, ParseDockerPortRange(normalized));
        }

        var hostAddress = normalized[..separatorIndex].Trim().Trim('[', ']');
        var hostPortText = normalized[(separatorIndex + 1)..].Trim();
        return (hostAddress, ParseDockerPortRange(hostPortText));
    }

    private static IReadOnlyList<int> ParseDockerPortRange(string value)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        var parts = normalized.Split('-', 2, StringSplitOptions.TrimEntries);
        if (!int.TryParse(parts[0], out var start) || start <= 0 || start > 65535)
        {
            return [];
        }

        if (parts.Length == 1)
        {
            return [start];
        }

        if (!int.TryParse(parts[1], out var end) || end < start || end > 65535)
        {
            return [start];
        }

        return Enumerable.Range(start, Math.Min(end - start + 1, 64)).ToArray();
    }

    private static int ParseFirstPort(string value)
    {
        var normalized = value.Trim();
        var separatorIndex = normalized.IndexOf('-');
        if (separatorIndex >= 0)
        {
            normalized = normalized[..separatorIndex];
        }

        return int.TryParse(normalized, out var port) ? port : 0;
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static string BuildDockerProbeGroupKey(DockerPublishedPort port)
    {
        var bind = NormalizeDockerBindAddress(port.HostAddress);
        return $"{ResolveDockerTargetHost(bind)}|{BuildDockerDisplayName(port)}";
    }

    private static string BuildDockerDisplayName(DockerPublishedPort port)
    {
        var image = string.IsNullOrWhiteSpace(port.Image) ? "container" : port.Image.Trim();
        return $"Docker: {port.ContainerName.Trim()} ({image}) -> {port.ContainerPort}";
    }

    private static string NormalizeDockerBindAddress(string value)
    {
        var normalized = value.Trim().Trim('[', ']');
        return normalized.Length == 0 ? "0.0.0.0" : normalized;
    }

    private static string ResolveDockerTargetHost(string bindAddress)
    {
        if (bindAddress is "0.0.0.0" or "::" or "*")
        {
            return "localhost";
        }

        if (IPAddress.TryParse(bindAddress, out var address) && IPAddress.IsLoopback(address))
        {
            return "localhost";
        }

        return bindAddress;
    }

    private static IPAddress? ResolveDockerProbeAddress(string bindAddress)
    {
        if (bindAddress is "0.0.0.0" or "::" or "*")
        {
            return IPAddress.Loopback;
        }

        if (IPAddress.TryParse(bindAddress, out var address))
        {
            return IPAddress.IsLoopback(address) ? IPAddress.Loopback : address;
        }

        return null;
    }

    private static string ResolveDockerDisplayAddress(string bindAddress)
    {
        if (bindAddress is "0.0.0.0" or "::" or "*")
        {
            return IPAddress.Loopback.ToString();
        }

        return IPAddress.TryParse(bindAddress, out var address) && IPAddress.IsLoopback(address)
            ? IPAddress.Loopback.ToString()
            : bindAddress;
    }

    private static IReadOnlyList<int> GetLocalListeningPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Where(endpoint => IsLocalHttpCandidateAddress(endpoint.Address))
                .Select(endpoint => endpoint.Port)
                .Where(port => port > 0)
                .Distinct()
                .Order()
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static bool IsLocalHttpCandidateAddress(IPAddress address) =>
        IPAddress.IsLoopback(address) ||
        address.Equals(IPAddress.Any) ||
        address.Equals(IPAddress.IPv6Any);

    private static async Task<string?> TryResolveHostNameAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            var lookupTask = Dns.GetHostEntryAsync(address);
            var completedTask = await Task.WhenAny(lookupTask, Task.Delay(ReverseLookupTimeout, cancellationToken));
            if (!ReferenceEquals(completedTask, lookupTask))
            {
                return null;
            }

            var hostEntry = await lookupTask;
            return NormalizeDnsName(hostEntry.HostName);
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<SshHostDiscoveryService.LanNeighbourEntry>> LoadLanNeighbourEntriesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "ip",
                    ["neigh", "show"],
                    false,
                    TimeSpan.FromSeconds(4),
                    "Inspect LAN neighbour cache for HTTP discovery")
                {
                    IsOptionalExternalTool = true
                },
                dryRun: false,
                cancellationToken);

            if (result.ExitCode == 0)
            {
                var entries = SshHostDiscoveryService.ParseLanNeighbourEntries(result.StandardOutput)
                    .Where(entry => IsUsableNeighbourState(entry.State))
                    .ToArray();
                if (entries.Length > 0)
                {
                    return entries;
                }
            }
        }
        catch
        {
        }

        return await LoadProcNetArpEntriesAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<SshHostDiscoveryService.LanNeighbourEntry>> LoadProcNetArpEntriesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            const string arpPath = "/proc/net/arp";
            if (!File.Exists(arpPath))
            {
                return [];
            }

            var lines = await File.ReadAllLinesAsync(arpPath, cancellationToken);
            var entries = new List<SshHostDiscoveryService.LanNeighbourEntry>();
            foreach (var line in lines.Skip(1))
            {
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length < 6 || !IPAddress.TryParse(tokens[0], out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                entries.Add(new SshHostDiscoveryService.LanNeighbourEntry(tokens[5], tokens[0], address, "REACHABLE"));
            }

            return entries
                .DistinctBy(entry => $"{entry.InterfaceName}|{entry.IpAddress}", StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<LanScanPlan> BuildLanScanPlans()
    {
        var plans = new List<LanScanPlan>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces()
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!ShouldIncludeLanInterface(networkInterface))
            {
                continue;
            }

            foreach (var unicastAddress in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (unicastAddress.Address.AddressFamily != AddressFamily.InterNetwork ||
                    !IsPrivateLanAddress(unicastAddress.Address))
                {
                    continue;
                }

                // Match the HA scanner's safety boundary: a /16 is already 65,534 hosts;
                // blindly expanding a /8 or /12 would turn a picker scan into a network sweep.
                if (unicastAddress.PrefixLength is < 16 or > 30)
                {
                    continue;
                }

                var prefixLength = unicastAddress.PrefixLength;
                plans.Add(new LanScanPlan(
                    networkInterface.Name,
                    FormatSubnet(unicastAddress.Address, prefixLength),
                    unicastAddress.Address,
                    prefixLength));
            }
        }

        return plans
            .DistinctBy(plan => $"{plan.InterfaceName}|{plan.SubnetLabel}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ShouldIncludeLanInterface(NetworkInterface networkInterface)
    {
        if (networkInterface.OperationalStatus != OperationalStatus.Up ||
            networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
            !networkInterface.Supports(NetworkInterfaceComponent.IPv4))
        {
            return false;
        }

        var name = networkInterface.Name.Trim();
        return !name.StartsWith("lo", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("docker", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("br-", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("veth", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("virbr", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("tailscale", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("zt", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("podman", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("lxcbr", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivateLanAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }

    private static bool IsAddressInSubnet(IPAddress candidate, IPAddress localAddress, int prefixLength)
    {
        var candidateValue = AddressToUInt32(candidate);
        var localValue = AddressToUInt32(localAddress);
        var mask = prefixLength == 0
            ? 0u
            : uint.MaxValue << (32 - prefixLength);

        return (candidateValue & mask) == (localValue & mask);
    }

    private static string FormatSubnet(IPAddress address, int prefixLength)
    {
        var networkValue = AddressToUInt32(address);
        var mask = prefixLength == 0
            ? 0u
            : uint.MaxValue << (32 - prefixLength);
        networkValue &= mask;
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, networkValue);
        return $"{new IPAddress(buffer)}/{prefixLength}";
    }

    private static uint AddressToUInt32(IPAddress address) =>
        BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    private static bool IsUsableNeighbourState(string state) =>
        !string.Equals(state, "FAILED", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(state, "INCOMPLETE", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(state, "NONE", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeDnsName(string? value)
    {
        var trimmed = value?.Trim().TrimEnd('.') ?? string.Empty;
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static async Task<(string? Title, string? Sample)> TryReadPageMetadataAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null ||
            (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
             !mediaType.Contains("text", StringComparison.OrdinalIgnoreCase)))
        {
            return (null, null);
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[8192];
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead <= 0)
            {
                return (null, null);
            }

            var sample = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var match = TitleRegex.Match(sample);
            if (!match.Success)
            {
                return (null, sample);
            }

            var title = WebUtility.HtmlDecode(match.Groups["title"].Value);
            return (string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), sample);
        }
        catch
        {
            return (null, null);
        }
    }

    private static async Task<string?> TryReadFaviconAsync(
        HttpClient client,
        string baseUrl,
        string? pageHtml,
        CancellationToken cancellationToken)
    {
        foreach (var faviconUrl in BuildFaviconCandidateUrls(baseUrl, pageHtml))
        {
            try
            {
                using var response = await client.GetAsync(
                    faviconUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                if (response.Content.Headers.ContentLength is > MaxFaviconBytes)
                {
                    continue;
                }

                var bytes = await ReadLimitedBytesAsync(response.Content, MaxFaviconBytes, cancellationToken);
                if (bytes.Length == 0 || bytes.Length > MaxFaviconBytes || !LooksLikeImage(bytes))
                {
                    continue;
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (string.IsNullOrWhiteSpace(mediaType) ||
                    !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    mediaType = GuessFaviconMediaType(faviconUrl, bytes);
                }

                return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
            }
            catch
            {
                // A favicon is presentation-only. Keep the discovered service when it is absent.
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildFaviconCandidateUrls(string baseUrl, string? pageHtml)
    {
        yield return $"{baseUrl.TrimEnd('/')}/favicon.ico";
        yield return $"{baseUrl.TrimEnd('/')}/apple-touch-icon.png";

        if (string.IsNullOrWhiteSpace(pageHtml) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            yield break;
        }

        foreach (Match linkMatch in FaviconLinkTagRegex.Matches(pageHtml))
        {
            var hrefMatch = FaviconHrefRegex.Match(linkMatch.Value);
            if (!hrefMatch.Success ||
                !Uri.TryCreate(baseUri, WebUtility.HtmlDecode(hrefMatch.Groups["href"].Value), out var faviconUri) ||
                !faviconUri.Scheme.Equals(baseUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !faviconUri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return faviconUri.ToString();
        }
    }

    private static async Task<byte[]> ReadLimitedBytesAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (buffer.Length <= maximumBytes)
        {
            var bytesRead = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken);
            if (buffer.Length > maximumBytes)
            {
                return [];
            }
        }

        return buffer.ToArray();
    }

    private static bool LooksLikeImage(byte[] bytes) =>
        (bytes.Length >= 8 &&
         bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
         bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A) ||
        (bytes.Length >= 6 &&
         (Encoding.ASCII.GetString(bytes, 0, 6) == "GIF87a" || Encoding.ASCII.GetString(bytes, 0, 6) == "GIF89a")) ||
        (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00) ||
        (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) ||
        (bytes.Length >= 4 && Encoding.ASCII.GetString(bytes, 0, 4).Equals("<svg", StringComparison.OrdinalIgnoreCase));

    private static string GuessFaviconMediaType(string faviconUrl, byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 6 &&
            (Encoding.ASCII.GetString(bytes, 0, 6) == "GIF87a" || Encoding.ASCII.GetString(bytes, 0, 6) == "GIF89a"))
        {
            return "image/gif";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        return faviconUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/x-icon";
    }

    private async Task<IReadOnlyList<LocalHttpServiceEndpoint>> ReadCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(storageSettings.CachePath))
            {
                return [];
            }

            await using var stream = File.OpenRead(storageSettings.CachePath);
            var cache = await JsonSerializer.DeserializeAsync<HttpServiceDiscoveryCache>(stream, JsonOptions, cancellationToken);
            return cache?.Endpoints ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task WriteCacheAsync(
        IReadOnlyList<LocalHttpServiceEndpoint> endpoints,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(storageSettings.RootDirectory);
        var cache = new HttpServiceDiscoveryCache(DateTimeOffset.UtcNow, SortEndpoints(endpoints));
        var temporaryPath = $"{storageSettings.CachePath}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, cache, JsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, storageSettings.CachePath, overwrite: true);
    }

    private static IReadOnlySet<string> BuildRequestedScopes(LocalHttpServiceDiscoveryRequest request)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (request.IncludeLocalhost)
        {
            scopes.Add("Localhost");
        }

        if (request.IncludeLan)
        {
            scopes.Add("LAN");
            scopes.Add("SSDP");
            scopes.Add("mDNS");
            scopes.Add("WS-Discovery");
        }

        if (request.IncludeTailnet)
        {
            scopes.Add("Tailnet");
        }

        if (request.IncludeDocker)
        {
            scopes.Add("Docker");
        }

        return scopes;
    }

    private static string BuildEndpointKey(LocalHttpServiceEndpoint endpoint) =>
        $"{endpoint.Scope}|{endpoint.Scheme}|{endpoint.Host}|{endpoint.Port}";

    private static string FormatEndpointForProgress(LocalHttpServiceEndpoint endpoint)
    {
        var name = !string.IsNullOrWhiteSpace(endpoint.Title)
            ? endpoint.Title
            : !string.IsNullOrWhiteSpace(endpoint.DisplayName)
                ? endpoint.DisplayName
                : endpoint.ServerHeader;

        return string.IsNullOrWhiteSpace(name)
            ? $"{endpoint.Host}:{endpoint.Port}"
            : $"{endpoint.Host}:{endpoint.Port} ({name})";
    }

    private static int BuildPresentationConfidence(string? title, string? faviconDataUrl, int statusCode)
    {
        if (statusCode is >= 400 or >= 300 and <= 399)
        {
            return 20;
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            return 70;
        }

        return string.IsNullOrWhiteSpace(faviconDataUrl) ? 45 : 55;
    }

    private static string BuildPresentationName(string? title, string? displayName, string host) =>
        FirstNonBlank(
            title,
            LocalHttpServiceDiscoveryRanking.IsSyntheticDiscoveryLabel(displayName) ? null : displayName,
            IPAddress.TryParse(host, out _) ? null : host) ?? string.Empty;

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static IReadOnlyList<string> BuildPresentationEvidence(string? title, string? serverHeader, string scope) =>
        new[]
        {
            string.IsNullOrWhiteSpace(title) ? null : $"Page title: {title.Trim()}.",
            string.IsNullOrWhiteSpace(serverHeader) ? null : $"Server header: {serverHeader.Trim()}.",
            $"Discovery scope: {scope}."
        }
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Cast<string>()
        .ToArray();

    private static string BuildUrl(string scheme, string host, int port) =>
        new UriBuilder(scheme, FormatHostForUri(host), port).Uri.ToString().TrimEnd('/');

    private static IReadOnlyList<int> BuildBaseScanPorts()
    {
        var ports = new List<int>(CommonHomelabPorts.Length + HttpsConventionPorts.Length + ExpandedPorts.Length);

        void AddRange(IEnumerable<int> values)
        {
            foreach (var port in values)
            {
                if (port is > 0 and <= 65535 && !ports.Contains(port))
                {
                    ports.Add(port);
                }
            }
        }

        AddRange(CommonHomelabPorts);
        AddRange(HttpsConventionPorts);
        AddRange(ExpandedPorts);
        return ports;
    }

    private static int[] BuildHttpsConventionPorts()
    {
        var ports = new List<int>(64);
        for (var thousands = 1; thousands <= 64; thousands++)
        {
            var port = thousands * 1000 + 443;
            if (port is > 0 and <= 65535)
            {
                ports.Add(port);
            }
        }

        return ports.ToArray();
    }

    private static IEnumerable<string> GuessSchemes(int port)
    {
        if (port == 443 || HttpsPreferredPorts.Contains(port) || port % 1000 == 443)
        {
            yield return Uri.UriSchemeHttps;
            yield return Uri.UriSchemeHttp;
        }
        else
        {
            yield return Uri.UriSchemeHttp;
            yield return Uri.UriSchemeHttps;
        }
    }

    private static string FormatHostForUri(string host) =>
        IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : host.Trim();

    private static IReadOnlyList<LocalHttpServiceEndpoint> SortEndpoints(IEnumerable<LocalHttpServiceEndpoint> endpoints) =>
        endpoints
            .DistinctBy(BuildEndpointKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(endpoint => LocalHttpServiceDiscoveryRanking.PresentationRank(endpoint))
            .ThenBy(endpoint => LocalHttpServiceDiscoveryRanking.IsUnknownLabel(endpoint.ServiceName) ? 1 : 0)
            .ThenBy(endpoint => endpoint.Exposure switch
            {
                DiscoveryExposure.Publishable => 0,
                DiscoveryExposure.RequiresManualConfirmation => 1,
                DiscoveryExposure.InternalOnly => 2,
                DiscoveryExposure.UnsafeToExpose => 3,
                _ => 4
            })
            .ThenByDescending(endpoint => endpoint.Confidence)
            .ThenBy(endpoint => endpoint.Scope switch
            {
                "Docker" => 0,
                "Localhost" => 1,
                "LAN" => 2,
                "Tailnet" => 3,
                _ => 4
            })
            .ThenBy(LocalHttpServiceDiscoveryRanking.PickerLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(endpoint => endpoint.Port)
            .ThenBy(endpoint => endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToArray();

    private sealed record HttpProbeHost(
        string TargetHost,
        IPAddress? ProbeAddress,
        string ProbeHost,
        string Scope,
        string? IpAddress,
        string? DisplayName,
        IReadOnlyList<int> Ports,
        bool IsLocalhostProbe,
        bool IsKnownLive,
        bool ResolveHostName);

    private sealed class HttpDiscoveryProgressState(int totalProbeCount)
    {
        private int foundCount;
        private int probedCount;

        public int TotalProbeCount { get; } = totalProbeCount;

        public int FoundCount => Volatile.Read(ref foundCount);

        public int ProbedCount => Volatile.Read(ref probedCount);

        public int IncrementFoundCount() => Interlocked.Increment(ref foundCount);

        public int IncrementProbedCount() => Interlocked.Increment(ref probedCount);
    }

    private sealed record LanScanPlan(
        string InterfaceName,
        string SubnetLabel,
        IPAddress LocalAddress,
        int PrefixLength);

    private sealed record DockerPublishedPort(
        string ContainerName,
        string? Image,
        string HostAddress,
        int HostPort,
        int ContainerPort);

    private sealed record HttpServiceDiscoveryCache(
        DateTimeOffset UpdatedAtUtc,
        IReadOnlyList<LocalHttpServiceEndpoint> Endpoints);

    private sealed class IPAddressComparer : IEqualityComparer<IPAddress>
    {
        public static readonly IPAddressComparer Instance = new();

        public bool Equals(IPAddress? x, IPAddress? y) =>
            x?.Equals(y) ?? y is null;

        public int GetHashCode(IPAddress obj) =>
            obj.GetHashCode();
    }
}
