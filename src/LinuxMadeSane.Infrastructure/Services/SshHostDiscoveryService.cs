using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SshHostDiscoveryService : ISshHostDiscoveryService
{
    private const int DefaultSshPort = 22;
    private const int MaxConcurrentProbes = 64;
    private const int MaxTailnetPeersToProbe = 64;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan BannerTimeout = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan ReverseLookupTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, CachedLanDiscoverySnapshot> LanDiscoveryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILinuxCommandRunner commandRunner;
    private readonly TailscalePeerStatusReader tailscalePeerStatusReader;

    public SshHostDiscoveryService(ILinuxCommandRunner commandRunner)
        : this(commandRunner, new TailscalePeerStatusReader(commandRunner))
    {
    }

    internal SshHostDiscoveryService(
        ILinuxCommandRunner commandRunner,
        TailscalePeerStatusReader tailscalePeerStatusReader)
    {
        this.commandRunner = commandRunner;
        this.tailscalePeerStatusReader = tailscalePeerStatusReader;
    }

    public Task<SshHostDiscoveryResult> GetCachedHostsAsync(
        SshHostDiscoveryScope scope,
        CancellationToken cancellationToken = default) =>
        scope switch
        {
            SshHostDiscoveryScope.Lan => Task.FromResult(GetCachedLanHostsResult()),
            SshHostDiscoveryScope.Tailnet => Task.FromResult(new SshHostDiscoveryResult(
                [],
                "No recent cached tailnet scan results are available yet.",
                ["Tailnet peers are read live from Tailscale when you run a tailnet scan."])),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported SSH host discovery scope.")
        };

    public Task<SshHostDiscoveryResult> DiscoverHostsAsync(
        SshHostDiscoveryScope scope,
        IProgress<SshHostDiscoveryProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default) =>
        scope switch
        {
            SshHostDiscoveryScope.Lan => DiscoverLanHostsAsync(progress, cancellationToken),
            SshHostDiscoveryScope.Tailnet => DiscoverTailnetHostsAsync(progress, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported SSH host discovery scope.")
        };

    private async Task<SshHostDiscoveryResult> DiscoverLanHostsAsync(
        IProgress<SshHostDiscoveryProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new SshHostDiscoveryProgressUpdate(
            SshHostDiscoveryStage.Preparing,
            "Inspecting active LAN interfaces for SSH discovery...",
            0,
            0,
            0,
            null,
            null));

        var scanPlans = BuildLanScanPlans();
        if (scanPlans.Count == 0)
        {
            return new SshHostDiscoveryResult(
                [],
                "LAN scan is unavailable because this LMS host does not currently have an active private IPv4 interface to probe.",
                ["LAN discovery only uses RFC1918 IPv4 addresses on the LMS server itself."]);
        }

        progress?.Report(new SshHostDiscoveryProgressUpdate(
            SshHostDiscoveryStage.ReadingNeighbourCache,
            "Reading the LMS server's ARP/neighbour cache to prioritise active address ranges...",
            0,
            0,
            0,
            null,
            null));

        var neighbourEntries = await LoadLanNeighbourEntriesAsync(cancellationToken);
        var cachedHosts = GetFreshCachedLanHosts(scanPlans);
        var discoveredHosts = new ConcurrentDictionary<string, DiscoveredSshHost>(StringComparer.OrdinalIgnoreCase);

        foreach (var host in cachedHosts)
        {
            discoveredHosts.TryAdd(BuildHostKey(host), host);
        }

        var batches = BuildLanProbeBatches(scanPlans, neighbourEntries);
        var totalCandidates = batches.Sum(batch => batch.Candidates.Count);
        if (totalCandidates == 0)
        {
            var noCandidateNotes = scanPlans
                .SelectMany(plan => plan.Notes)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            noCandidateNotes.Add("LAN discovery did not find any private IPv4 host addresses to probe.");

            return new SshHostDiscoveryResult(
                cachedHosts,
                cachedHosts.Count > 0
                    ? $"Reused {cachedHosts.Count} cached SSH host(s); the current LAN scan had no candidate addresses to probe."
                    : "LAN discovery did not find any host addresses to probe on the active private interfaces.",
                noCandidateNotes);
        }

        if (cachedHosts.Count > 0)
        {
            progress?.Report(new SshHostDiscoveryProgressUpdate(
                SshHostDiscoveryStage.Preparing,
                $"Loaded {cachedHosts.Count} cached SSH host(s) while refreshing the network scan...",
                0,
                totalCandidates,
                cachedHosts.Count,
                null,
                null));
        }

        var progressState = new LanDiscoveryProgressState(totalCandidates, cachedHosts.Count);
        foreach (var batch in batches)
        {
            progress?.Report(new SshHostDiscoveryProgressUpdate(
                batch.Stage,
                batch.StatusMessage,
                progressState.ProbedCount,
                totalCandidates,
                discoveredHosts.Count,
                batch.RangeLabel,
                null));

            await ProbeCandidateBatchAsync(
                batch,
                discoveredHosts,
                progress,
                progressState,
                resolveLanNames: true,
                cancellationToken);
        }

        var hosts = discoveredHosts.Values
            .OrderBy(host => host.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(host => host.Target, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        UpdateLanDiscoveryCache(scanPlans, hosts);

        var notes = scanPlans
            .SelectMany(plan => plan.Notes)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (neighbourEntries.Count > 0)
        {
            notes.Add("LAN discovery used the LMS server's ARP/neighbour cache to probe known devices first and to rank the busiest /24 ranges before the wider subnet mop-up.");
        }

        notes.Add("LAN discovery probes the full configured private IPv4 subnet for each active interface and only keeps endpoints that answer with an SSH banner on port 22.");

        if (cachedHosts.Count > 0)
        {
            notes.Add($"Preloaded {cachedHosts.Count} cached SSH host(s) while the live scan refreshed the subnet.");
        }

        var statusMessage = hosts.Length > 0
            ? $"Discovered {hosts.Length} SSH host(s) across {scanPlans.Count} active LAN subnet(s)."
            : $"Scanned {totalCandidates} LAN address(es) across {scanPlans.Count} subnet(s); no SSH banners were found on port 22.";

        progress?.Report(new SshHostDiscoveryProgressUpdate(
            SshHostDiscoveryStage.Completed,
            statusMessage,
            totalCandidates,
            totalCandidates,
            hosts.Length,
            null,
            null));

        return new SshHostDiscoveryResult(hosts, statusMessage, notes);
    }

    private static SshHostDiscoveryResult GetCachedLanHostsResult()
    {
        var scanPlans = BuildLanScanPlans();
        if (scanPlans.Count == 0)
        {
            return new SshHostDiscoveryResult(
                [],
                "LAN host cache is unavailable because this LMS host does not currently have an active private IPv4 interface.",
                ["LAN discovery only uses RFC1918 IPv4 addresses on the LMS server itself."]);
        }

        var cachedHosts = GetFreshCachedLanHosts(scanPlans);
        if (cachedHosts.Count == 0)
        {
            return new SshHostDiscoveryResult(
                [],
                "No recent cached LAN SSH hosts are available yet.",
                ["Run Scan LAN once to populate the recent cache for this LMS host."]);
        }

        return new SshHostDiscoveryResult(
            cachedHosts,
            $"Showing {cachedHosts.Count} recently cached SSH host(s) while the LAN scan refreshes.",
            ["Recent LAN scan results are reused for up to 10 minutes and refreshed in the background by the next scan."]);
    }

    private async Task<SshHostDiscoveryResult> DiscoverTailnetHostsAsync(
        IProgress<SshHostDiscoveryProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new SshHostDiscoveryProgressUpdate(
            SshHostDiscoveryStage.Preparing,
            "Reading Tailscale peers for SSH discovery...",
            0,
            0,
            0,
            null,
            null));

        var tailnetStatus = await tailscalePeerStatusReader.ReadAsync(cancellationToken);
        if (!tailnetStatus.IsReady)
        {
            return tailnetStatus.Availability switch
            {
                TailscalePeerStatusAvailability.NoInterface => new SshHostDiscoveryResult(
                    [],
                    "Tailnet scan is unavailable because this LMS host does not currently have an active Tailscale interface.",
                    ["Bring the LMS server onto the tailnet first, then retry the tailnet scan."]),
                TailscalePeerStatusAvailability.CliMissing => new SshHostDiscoveryResult(
                    [],
                    "Tailnet scan is unavailable because `tailscale` is not installed on this LMS host.",
                    ["Tailnet discovery becomes available automatically when the LMS server has the Tailscale CLI installed and logged in."]),
                _ => new SshHostDiscoveryResult(
                    [],
                    $"Tailnet scan failed: {tailnetStatus.FailureMessage}",
                    ["Tailnet discovery uses `tailscale status --json` before probing peers on TCP/22."])
            };
        }

        var peers = tailnetStatus.Peers;
        if (peers.Count == 0)
        {
            return new SshHostDiscoveryResult(
                [],
                "Tailscale is installed, but this LMS host does not currently see any tailnet peers with IPv4 addresses.",
                ["Tailnet discovery only probes peers that expose a Tailscale IPv4 address."]);
        }

        var onlinePeers = peers
            .Where(peer => peer.Online || peer.Active)
            .ToArray();
        var skippedPeers = peers.Count - onlinePeers.Length;

        if (onlinePeers.Length == 0)
        {
            return new SshHostDiscoveryResult(
                [],
                $"Tailscale reported {peers.Count} peer(s), but none are currently online to probe for SSH.",
                ["Tailnet discovery only probes peers that `tailscale status` currently reports as online or active."]);
        }

        var peersToProbe = onlinePeers
            .Take(MaxTailnetPeersToProbe)
            .Select(peer => new DiscoveryCandidate(
                peer.DisplayName,
                peer.Target,
                peer.IpAddress,
                IPAddress.Parse(peer.IpAddress),
                DefaultSshPort,
                "Tailnet",
                string.IsNullOrWhiteSpace(peer.OwnerLabel)
                    ? "Tailnet peer"
                    : $"Tailnet peer owned by {peer.OwnerLabel}",
                peer.Platform))
            .ToArray();

        var notes = new List<string>();
        if (skippedPeers > 0)
        {
            notes.Add($"Skipped {skippedPeers} tailnet peer(s) that Tailscale did not report as online.");
        }

        if (onlinePeers.Length > MaxTailnetPeersToProbe)
        {
            notes.Add($"Limited the scan to the first {MaxTailnetPeersToProbe} online tailnet peers so the lookup stays quick.");
        }

        notes.Add("Tailnet discovery uses `tailscale status --json` to list peers, then confirms SSH by probing TCP/22.");

        var progressState = new LanDiscoveryProgressState(peersToProbe.Length, 0);
        var discoveredHosts = new ConcurrentDictionary<string, DiscoveredSshHost>(StringComparer.OrdinalIgnoreCase);
        var batch = new DiscoveryBatch(
            SshHostDiscoveryStage.ProbingPrioritizedBlocks,
            "Tailnet peers",
            $"Probing {peersToProbe.Length} online tailnet peer(s) for SSH...",
            peersToProbe);

        progress?.Report(new SshHostDiscoveryProgressUpdate(
            batch.Stage,
            batch.StatusMessage,
            0,
            peersToProbe.Length,
            0,
            batch.RangeLabel,
            null));

        await ProbeCandidateBatchAsync(
            batch,
            discoveredHosts,
            progress,
            progressState,
            resolveLanNames: false,
            cancellationToken);

        var hosts = discoveredHosts.Values
            .OrderBy(host => host.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(host => host.Target, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var statusMessage = hosts.Length > 0
            ? $"Discovered {hosts.Length} SSH host(s) from {peersToProbe.Length} online tailnet peer(s)."
            : $"Probed {peersToProbe.Length} online tailnet peer(s); none answered with an SSH banner on port 22.";

        progress?.Report(new SshHostDiscoveryProgressUpdate(
            SshHostDiscoveryStage.Completed,
            statusMessage,
            peersToProbe.Length,
            peersToProbe.Length,
            hosts.Length,
            null,
            null));

        return new SshHostDiscoveryResult(hosts, statusMessage, notes);
    }

    internal static IReadOnlyList<TailnetPeer> ParseTailnetPeers(string json)
        =>
        TailscalePeerStatusReader.ParseTailnetPeers(json);

    internal static IReadOnlyList<IPAddress> BuildLanCandidateAddresses(IPAddress localAddress, int prefixLength)
    {
        if (localAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return [];
        }

        var normalizedPrefixLength = Math.Clamp(prefixLength, 0, 30);
        if (normalizedPrefixLength >= 31)
        {
            return [];
        }

        var addressBytes = localAddress.GetAddressBytes();
        var addressValue = BinaryPrimitives.ReadUInt32BigEndian(addressBytes);
        var mask = normalizedPrefixLength == 0
            ? 0u
            : uint.MaxValue << (32 - normalizedPrefixLength);
        var network = addressValue & mask;
        var broadcast = network | ~mask;
        var firstHost = network + 1;
        var lastHost = broadcast - 1;

        if (lastHost < firstHost)
        {
            return [];
        }

        var addresses = new List<IPAddress>();
        Span<byte> buffer = stackalloc byte[4];
        for (var current = firstHost; current <= lastHost; current++)
        {
            if (current == addressValue)
            {
                continue;
            }

            BinaryPrimitives.WriteUInt32BigEndian(buffer, current);
            addresses.Add(new IPAddress(buffer));
        }

        return addresses;
    }

    internal static IReadOnlyList<LanNeighbourEntry> ParseLanNeighbourEntries(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var entries = new List<LanNeighbourEntry>();
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length < 3 || !IPAddress.TryParse(tokens[0], out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            var devIndex = Array.IndexOf(tokens, "dev");
            if (devIndex < 0 || devIndex + 1 >= tokens.Length)
            {
                continue;
            }

            var interfaceName = tokens[devIndex + 1];
            var state = tokens.LastOrDefault(token => KnownNeighbourStates.Contains(token)) ?? "UNKNOWN";
            entries.Add(new LanNeighbourEntry(interfaceName, tokens[0], address, state));
        }

        return entries
            .DistinctBy(entry => $"{entry.InterfaceName}|{entry.IpAddress}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task ProbeCandidateBatchAsync(
        DiscoveryBatch batch,
        ConcurrentDictionary<string, DiscoveredSshHost> discoveredHosts,
        IProgress<SshHostDiscoveryProgressUpdate>? progress,
        LanDiscoveryProgressState progressState,
        bool resolveLanNames,
        CancellationToken cancellationToken)
    {
        using var limiter = new SemaphoreSlim(MaxConcurrentProbes);

        var tasks = batch.Candidates.Select(async candidate =>
        {
            await limiter.WaitAsync(cancellationToken);

            try
            {
                var host = await ProbeCandidateAsync(candidate, resolveLanNames, cancellationToken);
                var probedCount = progressState.IncrementProbedCount();

                DiscoveredSshHost? reportedHost = null;
                if (host is not null && discoveredHosts.TryAdd(BuildHostKey(host), host))
                {
                    reportedHost = host;
                    progressState.IncrementDiscoveredCount();
                }

                if (reportedHost is not null || progressState.ShouldReportStatus())
                {
                    progress?.Report(new SshHostDiscoveryProgressUpdate(
                        batch.Stage,
                        BuildBatchProgressMessage(batch, probedCount, progressState.TotalCandidates, progressState.DiscoveredCount),
                        probedCount,
                        progressState.TotalCandidates,
                        progressState.DiscoveredCount,
                        batch.RangeLabel,
                        reportedHost));
                }
            }
            finally
            {
                limiter.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task<DiscoveredSshHost?> ProbeCandidateAsync(
        DiscoveryCandidate candidate,
        bool resolveLanNames,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient(candidate.ProbeAddress.AddressFamily);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(ConnectTimeout);

        try
        {
            await client.ConnectAsync(candidate.ProbeAddress, candidate.Port, connectCts.Token);
        }
        catch
        {
            return null;
        }

        string? sshBanner;
        try
        {
            sshBanner = await ReadSshBannerAsync(client, cancellationToken);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(sshBanner))
        {
            return null;
        }

        var displayName = candidate.DisplayName;
        var target = candidate.Target;
        if (resolveLanNames)
        {
            var resolvedHostName = await TryResolveHostNameAsync(candidate.ProbeAddress, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolvedHostName))
            {
                target = resolvedHostName;
                displayName = TailnetPeerNameFormatter.BuildDisplayName(
                    customName: null,
                    hostName: resolvedHostName,
                    dnsName: resolvedHostName,
                    ipAddress: candidate.IpAddress,
                    ownerLabel: null,
                    fallbackLabel: "SSH host");
            }
        }

        return new DiscoveredSshHost(
            displayName,
            target,
            candidate.IpAddress,
            candidate.Port,
            candidate.ScopeLabel,
            candidate.SourceLabel,
            candidate.Platform,
            sshBanner);
    }

    private static async Task<string?> ReadSshBannerAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var bannerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bannerCts.CancelAfter(BannerTimeout);

        using var stream = client.GetStream();
        var buffer = new byte[256];
        var totalBytes = 0;

        while (totalBytes < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalBytes, buffer.Length - totalBytes), bannerCts.Token);
            if (bytesRead <= 0)
            {
                break;
            }

            totalBytes += bytesRead;
            if (Array.IndexOf(buffer, (byte)'\n', 0, totalBytes) >= 0)
            {
                break;
            }
        }

        if (totalBytes == 0)
        {
            return null;
        }

        var rawBanner = Encoding.ASCII.GetString(buffer, 0, totalBytes)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(rawBanner) || !rawBanner.StartsWith("SSH-", StringComparison.OrdinalIgnoreCase)
            ? null
            : rawBanner.Trim();
    }

    private async Task<IReadOnlyList<LanNeighbourEntry>> LoadLanNeighbourEntriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "ip",
                    ["neigh", "show"],
                    false,
                    TimeSpan.FromSeconds(4),
                    "Inspect LAN neighbour cache for SSH discovery")
                {
                    IsOptionalExternalTool = true
                },
                dryRun: false,
                cancellationToken);

            if (result.ExitCode == 0)
            {
                var entries = ParseLanNeighbourEntries(result.StandardOutput);
                if (entries.Count > 0)
                {
                    return entries
                        .Where(entry => IsUsableNeighbourState(entry.State))
                        .ToArray();
                }
            }
        }
        catch
        {
        }

        try
        {
            const string arpPath = "/proc/net/arp";
            if (!File.Exists(arpPath))
            {
                return [];
            }

            var lines = await File.ReadAllLinesAsync(arpPath, cancellationToken);
            return ParseProcNetArpEntries(lines);
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
                if (unicastAddress.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                if (!IsPrivateLanAddress(unicastAddress.Address))
                {
                    continue;
                }

                var effectivePrefixLength = Math.Clamp(unicastAddress.PrefixLength, 1, 30);
                var addresses = BuildLanCandidateAddresses(unicastAddress.Address, effectivePrefixLength);
                if (addresses.Count == 0)
                {
                    continue;
                }

                plans.Add(new LanScanPlan(
                    networkInterface.Name,
                    FormatSubnet(unicastAddress.Address, effectivePrefixLength),
                    unicastAddress.Address,
                    effectivePrefixLength,
                    addresses,
                    []));
            }
        }

        return plans
            .GroupBy(plan => $"{plan.InterfaceName}|{plan.SubnetLabel}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static IReadOnlyList<DiscoveryBatch> BuildLanProbeBatches(
        IReadOnlyList<LanScanPlan> scanPlans,
        IReadOnlyList<LanNeighbourEntry> neighbourEntries)
    {
        var batches = new List<DiscoveryBatch>();

        foreach (var plan in scanPlans)
        {
            var candidateSourceLabel = $"Subnet {plan.SubnetLabel} via {plan.InterfaceName}";
            var neighbourMatches = neighbourEntries
                .Where(entry =>
                    entry.InterfaceName.Equals(plan.InterfaceName, StringComparison.OrdinalIgnoreCase) &&
                    IsAddressInSubnet(entry.Address, plan.LocalAddress, plan.PrefixLength))
                .OrderBy(entry => entry.Address, IPEndPointComparer.Instance)
                .ToArray();

            var neighbourAddressSet = neighbourMatches
                .Select(entry => entry.Address.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (neighbourMatches.Length > 0)
            {
                batches.Add(new DiscoveryBatch(
                    SshHostDiscoveryStage.ProbingNeighbourHosts,
                    $"{plan.SubnetLabel} known neighbours",
                    $"Probing ARP-known addresses in {plan.SubnetLabel} via {plan.InterfaceName}...",
                    neighbourMatches
                        .Select(entry => new DiscoveryCandidate(
                            entry.IpAddress,
                            entry.IpAddress,
                            entry.IpAddress,
                            entry.Address,
                            DefaultSshPort,
                            "LAN",
                            candidateSourceLabel,
                            null))
                        .ToArray()));
            }

            var addressBlocks = plan.Addresses
                .Where(address => !neighbourAddressSet.Contains(address.ToString()))
                .GroupBy(address => FormatSlash24Block(address))
                .Select(group =>
                {
                    var neighbourCount = neighbourMatches.Count(entry => FormatSlash24Block(entry.Address) == group.Key);
                    return new LanAddressBlock(group.Key, neighbourCount, group.OrderBy(address => address, IPEndPointComparer.Instance).ToArray());
                })
                .OrderByDescending(block => block.NeighbourCount)
                .ThenBy(block => block.BlockLabel, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var block in addressBlocks.Where(block => block.NeighbourCount > 0))
            {
                batches.Add(new DiscoveryBatch(
                    SshHostDiscoveryStage.ProbingPrioritizedBlocks,
                    $"{block.BlockLabel} via {plan.InterfaceName}",
                    $"Scanning active {block.BlockLabel} addresses inside {plan.SubnetLabel} via {plan.InterfaceName}...",
                    block.Addresses
                        .Select(address => new DiscoveryCandidate(
                            address.ToString(),
                            address.ToString(),
                            address.ToString(),
                            address,
                            DefaultSshPort,
                            "LAN",
                            candidateSourceLabel,
                            null))
                        .ToArray()));
            }

            foreach (var block in addressBlocks.Where(block => block.NeighbourCount == 0))
            {
                batches.Add(new DiscoveryBatch(
                    SshHostDiscoveryStage.ProbingRemainingBlocks,
                    $"{block.BlockLabel} via {plan.InterfaceName}",
                    $"Mopping up the remaining {block.BlockLabel} addresses inside {plan.SubnetLabel} via {plan.InterfaceName}...",
                    block.Addresses
                        .Select(address => new DiscoveryCandidate(
                            address.ToString(),
                            address.ToString(),
                            address.ToString(),
                            address,
                            DefaultSshPort,
                            "LAN",
                            candidateSourceLabel,
                            null))
                        .ToArray()));
            }
        }

        return batches;
    }

    private static IReadOnlyList<DiscoveredSshHost> GetFreshCachedLanHosts(IReadOnlyList<LanScanPlan> scanPlans)
    {
        var cutoff = DateTimeOffset.UtcNow - CacheLifetime;
        var hosts = new List<DiscoveredSshHost>();

        foreach (var plan in scanPlans)
        {
            var cacheKey = BuildLanCacheKey(plan);
            if (!LanDiscoveryCache.TryGetValue(cacheKey, out var snapshot))
            {
                continue;
            }

            if (snapshot.CapturedAtUtc < cutoff)
            {
                LanDiscoveryCache.TryRemove(cacheKey, out _);
                continue;
            }

            hosts.AddRange(snapshot.Hosts);
        }

        return hosts
            .DistinctBy(BuildHostKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(host => host.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(host => host.Target, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void UpdateLanDiscoveryCache(IReadOnlyList<LanScanPlan> scanPlans, IReadOnlyList<DiscoveredSshHost> hosts)
    {
        var capturedAtUtc = DateTimeOffset.UtcNow;

        foreach (var plan in scanPlans)
        {
            var sourceLabel = $"Subnet {plan.SubnetLabel} via {plan.InterfaceName}";
            var planHosts = hosts
                .Where(host => host.ScopeLabel == "LAN" &&
                               host.SourceLabel.Equals(sourceLabel, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            LanDiscoveryCache[BuildLanCacheKey(plan)] = new CachedLanDiscoverySnapshot(capturedAtUtc, planHosts);
        }
    }

    private static IReadOnlyList<LanNeighbourEntry> ParseProcNetArpEntries(IReadOnlyList<string> lines)
    {
        if (lines.Count <= 1)
        {
            return [];
        }

        var entries = new List<LanNeighbourEntry>();
        foreach (var line in lines.Skip(1))
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length < 6 || !IPAddress.TryParse(tokens[0], out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            if (string.Equals(tokens[2], "0x0", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries.Add(new LanNeighbourEntry(tokens[5], tokens[0], address, "REACHABLE"));
        }

        return entries
            .DistinctBy(entry => $"{entry.InterfaceName}|{entry.IpAddress}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ShouldIncludeLanInterface(NetworkInterface networkInterface)
    {
        if (networkInterface.OperationalStatus != OperationalStatus.Up)
        {
            return false;
        }

        if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return false;
        }

        if (!networkInterface.Supports(NetworkInterfaceComponent.IPv4))
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
        var candidateValue = BinaryPrimitives.ReadUInt32BigEndian(candidate.GetAddressBytes());
        var localValue = BinaryPrimitives.ReadUInt32BigEndian(localAddress.GetAddressBytes());
        var mask = prefixLength == 0
            ? 0u
            : uint.MaxValue << (32 - prefixLength);

        return (candidateValue & mask) == (localValue & mask);
    }

    private static string FormatSubnet(IPAddress address, int prefixLength)
    {
        var networkBytes = address.GetAddressBytes();
        var networkValue = BinaryPrimitives.ReadUInt32BigEndian(networkBytes);
        var mask = prefixLength == 0
            ? 0u
            : uint.MaxValue << (32 - prefixLength);
        networkValue &= mask;
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, networkValue);
        return $"{new IPAddress(buffer)}/{prefixLength}";
    }

    private static string FormatSlash24Block(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
    }

    private static bool IsUsableNeighbourState(string state) =>
        !string.Equals(state, "FAILED", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(state, "INCOMPLETE", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(state, "NONE", StringComparison.OrdinalIgnoreCase);

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

    private static string? NormalizeDnsName(string? value)
    {
        var trimmed = value?.Trim().TrimEnd('.') ?? string.Empty;
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string BuildBatchProgressMessage(DiscoveryBatch batch, int probedCount, int totalCandidates, int discoveredCount) =>
        $"{batch.StatusMessage} {probedCount}/{totalCandidates} address(es) probed, {discoveredCount} SSH host(s) found.";

    private static string BuildLanCacheKey(LanScanPlan plan) =>
        $"{plan.InterfaceName}|{plan.SubnetLabel}";

    private static string BuildHostKey(DiscoveredSshHost host) =>
        string.IsNullOrWhiteSpace(host.IpAddress)
            ? host.Target
            : host.IpAddress;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildFailureMessage(LinuxCommandResult commandResult)
    {
        if (!string.IsNullOrWhiteSpace(commandResult.StandardError))
        {
            return commandResult.StandardError.Trim();
        }

        if (!string.IsNullOrWhiteSpace(commandResult.StandardOutput))
        {
            return commandResult.StandardOutput.Trim();
        }

        return $"Command exited with status {commandResult.ExitCode}.";
    }

    internal sealed record LanNeighbourEntry(
        string InterfaceName,
        string IpAddress,
        IPAddress Address,
        string State);

    private sealed record DiscoveryCandidate(
        string DisplayName,
        string Target,
        string IpAddress,
        IPAddress ProbeAddress,
        int Port,
        string ScopeLabel,
        string SourceLabel,
        string? Platform);

    private sealed record LanScanPlan(
        string InterfaceName,
        string SubnetLabel,
        IPAddress LocalAddress,
        int PrefixLength,
        IReadOnlyList<IPAddress> Addresses,
        IReadOnlyList<string> Notes);

    private sealed record LanAddressBlock(
        string BlockLabel,
        int NeighbourCount,
        IReadOnlyList<IPAddress> Addresses);

    private sealed record DiscoveryBatch(
        SshHostDiscoveryStage Stage,
        string RangeLabel,
        string StatusMessage,
        IReadOnlyList<DiscoveryCandidate> Candidates);

    private sealed record CachedLanDiscoverySnapshot(
        DateTimeOffset CapturedAtUtc,
        IReadOnlyList<DiscoveredSshHost> Hosts);

    private sealed class LanDiscoveryProgressState(int totalCandidates, int discoveredCount)
    {
        private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);
        private int probedCount;
        private int currentDiscoveredCount = discoveredCount;
        private long lastReportTicks = DateTimeOffset.UtcNow.UtcTicks;

        public int TotalCandidates { get; } = totalCandidates;

        public int ProbedCount => Volatile.Read(ref probedCount);

        public int DiscoveredCount => Volatile.Read(ref currentDiscoveredCount);

        public int IncrementProbedCount() => Interlocked.Increment(ref probedCount);

        public void IncrementDiscoveredCount() => Interlocked.Increment(ref currentDiscoveredCount);

        public bool ShouldReportStatus()
        {
            if (ProbedCount % 32 == 0)
            {
                return true;
            }

            var now = DateTimeOffset.UtcNow.UtcTicks;
            var previous = Interlocked.Read(ref lastReportTicks);
            if (now - previous < ReportInterval.Ticks)
            {
                return false;
            }

            Interlocked.Exchange(ref lastReportTicks, now);
            return true;
        }
    }

    private sealed class IPEndPointComparer : IComparer<IPAddress>
    {
        public static readonly IPEndPointComparer Instance = new();

        public int Compare(IPAddress? x, IPAddress? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var xValue = BinaryPrimitives.ReadUInt32BigEndian(x.GetAddressBytes());
            var yValue = BinaryPrimitives.ReadUInt32BigEndian(y.GetAddressBytes());
            return xValue.CompareTo(yValue);
        }
    }

    private static readonly HashSet<string> KnownNeighbourStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "REACHABLE",
        "STALE",
        "DELAY",
        "PROBE",
        "FAILED",
        "INCOMPLETE",
        "PERMANENT",
        "NOARP",
        "NONE"
    };
}
