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
    private const int MaxConcurrentProbes = 96;
    private const int MaxLanCandidates = 512;
    private const int MaxTailnetPeersToProbe = 96;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1400);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan ReverseLookupTimeout = TimeSpan.FromMilliseconds(280);
    private static readonly int[] CommonHttpPorts =
    [
        80, 443, 3000, 3001, 5000, 5001, 5080, 7000, 7126, 8000, 8080, 8081, 8123, 8443, 8888, 9000, 9090, 9443, 10000,
        11434, 32400
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly Regex TitleRegex = new(
        "<title[^>]*>\\s*(?<title>.*?)\\s*</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

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

        if (request.IncludeLan)
        {
            hosts.AddRange(await BuildLanProbeHostsAsync(cancellationToken));
        }

        if (request.IncludeTailnet)
        {
            hosts.AddRange(await BuildTailnetProbeHostsAsync(cancellationToken));
        }

        var totalProbeCount = hosts.Sum(host => host.Ports.Count);
        progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
            totalProbeCount == 0
                ? "No HTTP/S scan targets were available."
                : $"Scanning {totalProbeCount} host/port combination(s)...",
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

        var progressState = new HttpDiscoveryProgressState(hosts.Sum(host => host.Ports.Count));
        var results = new ConcurrentDictionary<string, LocalHttpServiceEndpoint>(StringComparer.OrdinalIgnoreCase);
        using var concurrency = new SemaphoreSlim(MaxConcurrentProbes);
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

        var tasks = hosts
            .SelectMany(host => host.Ports.Select(port => ProbePortAsync(
                client,
                concurrency,
                results,
                host,
                port,
                progress,
                progressState,
                cancellationToken)))
            .ToArray();

        await Task.WhenAll(tasks);
        return results.Values.ToArray();
    }

    private static async Task ProbePortAsync(
        HttpClient client,
        SemaphoreSlim concurrency,
        ConcurrentDictionary<string, LocalHttpServiceEndpoint> results,
        HttpProbeHost host,
        int port,
        IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
        HttpDiscoveryProgressState progressState,
        CancellationToken cancellationToken)
    {
        await concurrency.WaitAsync(cancellationToken);
        try
        {
            if (!host.IsLocalhostProbe &&
                (host.ProbeAddress is null || !await CanOpenTcpAsync(host.ProbeAddress, port, cancellationToken)))
            {
                return;
            }

            foreach (var scheme in new[] { Uri.UriSchemeHttps, Uri.UriSchemeHttp })
            {
                var endpoint = await TryProbeAsync(client, host, scheme, port, cancellationToken);
                if (endpoint is null)
                {
                    continue;
                }

                if (results.TryAdd(BuildEndpointKey(endpoint), endpoint))
                {
                    var foundCount = progressState.IncrementFoundCount();
                    progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                        $"Found {FormatEndpointForProgress(endpoint)}",
                        progressState.ProbedCount,
                        progressState.TotalProbeCount,
                        foundCount,
                        endpoint));
                }
            }
        }
        finally
        {
            var probedCount = progressState.IncrementProbedCount();
            if (ShouldReportProbeProgress(probedCount, progressState.TotalProbeCount))
            {
                progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                    $"Checked {probedCount} of {progressState.TotalProbeCount} host/port combination(s).",
                    probedCount,
                    progressState.TotalProbeCount,
                    progressState.FoundCount));
            }

            concurrency.Release();
        }
    }

    private static async Task<bool> CanOpenTcpAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(address.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);

        try
        {
            await client.ConnectAsync(address, port, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
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

            var title = await TryReadTitleAsync(response, timeout.Token);
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
                DateTimeOffset.UtcNow);
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
                    "Local LMS host",
                    ports,
                    IsLocalhostProbe: true,
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
        var candidates = new List<(IPAddress Address, string SourceLabel, bool IsNeighbour)>();

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
                candidates.Add((address, $"{plan.SubnetLabel} known neighbour", true));
            }

            var neighbourBlocks = neighbourAddresses
                .Select(FormatSlash24Block)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var localBlock = FormatSlash24Block(plan.LocalAddress);
            var includeFullSubnet = plan.PrefixLength >= 24;
            var subnetAddresses = SshHostDiscoveryService.BuildLanCandidateAddresses(plan.LocalAddress, plan.PrefixLength)
                .Where(address =>
                    includeFullSubnet ||
                    FormatSlash24Block(address).Equals(localBlock, StringComparison.OrdinalIgnoreCase) ||
                    neighbourBlocks.Contains(FormatSlash24Block(address)))
                .OrderBy(AddressToUInt32)
                .Take(MaxLanCandidates)
                .ToArray();

            foreach (var address in subnetAddresses)
            {
                candidates.Add((address, $"{plan.SubnetLabel} via {plan.InterfaceName}", false));
            }
        }

        return candidates
            .GroupBy(candidate => candidate.Address.ToString(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Any(candidate => candidate.IsNeighbour))
            .ThenBy(group => AddressToUInt32(IPAddress.Parse(group.Key)))
            .Take(MaxLanCandidates)
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
                    item.SourceLabel,
                    CommonHttpPorts,
                    IsLocalhostProbe: false,
                    ResolveHostName: true);
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
                    CommonHttpPorts,
                    IsLocalhostProbe: false,
                    ResolveHostName: false);
            })
            .ToArray();
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

                var prefixLength = Math.Clamp(unicastAddress.PrefixLength, 1, 30);
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

    private static string FormatSlash24Block(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
    }

    private static bool IsUsableNeighbourState(string state) =>
        !string.Equals(state, "FAILED", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(state, "INCOMPLETE", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(state, "NONE", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeDnsName(string? value)
    {
        var trimmed = value?.Trim().TrimEnd('.') ?? string.Empty;
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static async Task<string?> TryReadTitleAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null ||
            (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
             !mediaType.Contains("text", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[8192];
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead <= 0)
            {
                return null;
            }

            var sample = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var match = TitleRegex.Match(sample);
            if (!match.Success)
            {
                return null;
            }

            var title = WebUtility.HtmlDecode(match.Groups["title"].Value);
            return string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        catch
        {
            return null;
        }
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
        }

        if (request.IncludeTailnet)
        {
            scopes.Add("Tailnet");
        }

        return scopes;
    }

    private static string BuildEndpointKey(LocalHttpServiceEndpoint endpoint) =>
        $"{endpoint.Scope}|{endpoint.Scheme}|{endpoint.Host}|{endpoint.Port}";

    private static bool ShouldReportProbeProgress(int probedCount, int totalProbeCount) =>
        totalProbeCount == 0 ||
        probedCount == totalProbeCount ||
        probedCount % 20 == 0;

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

    private static string BuildUrl(string scheme, string host, int port) =>
        new UriBuilder(scheme, FormatHostForUri(host), port).Uri.ToString().TrimEnd('/');

    private static string FormatHostForUri(string host) =>
        IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : host.Trim();

    private static IReadOnlyList<LocalHttpServiceEndpoint> SortEndpoints(IEnumerable<LocalHttpServiceEndpoint> endpoints) =>
        endpoints
            .DistinctBy(BuildEndpointKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(endpoint => endpoint.Scope switch
            {
                "Localhost" => 0,
                "LAN" => 1,
                "Tailnet" => 2,
                _ => 3
            })
            .ThenBy(endpoint => endpoint.DisplayName ?? endpoint.Host, StringComparer.OrdinalIgnoreCase)
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
