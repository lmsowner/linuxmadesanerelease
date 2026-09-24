// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Infrastructure.Services;

internal sealed class SambaNetworkDiscoveryService(
    ILinuxCommandRunner commandRunner,
    TailscalePeerStatusReader? tailscalePeerStatusReader = null)
{
    private static readonly Regex FindSmbLinePattern = new(
        @"^(?<ip>\S+)\s+(?<name>\S+)(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BracketValuePattern = new(
        @"(?<prefix>[+*]?)[\[](?<value>[^\]]+)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TailscalePingViaPattern = new(
        @"\bvia\s+(?<ip>\d{1,3}(?:\.\d{1,3}){3})(?::\d+)?\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly TailscalePeerStatusReader tailscalePeerStatusReader =
        tailscalePeerStatusReader ?? new TailscalePeerStatusReader(commandRunner);

    public Task<NetworkShareMachineDiscoveryResult> DiscoverMachinesAsync(
        NetworkShareDiscoveryScope scope = NetworkShareDiscoveryScope.Lan,
        CancellationToken cancellationToken = default) =>
        scope switch
        {
            NetworkShareDiscoveryScope.Lan => DiscoverLanMachinesAsync(cancellationToken),
            NetworkShareDiscoveryScope.Tailnet => DiscoverTailnetMachinesAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported share discovery scope.")
        };

    private async Task<NetworkShareMachineDiscoveryResult> DiscoverLanMachinesAsync(
        CancellationToken cancellationToken)
    {
        var machines = new Dictionary<string, NetworkShareMachine>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();

        var avahiResult = await RunCommandAsync(
            "avahi-browse",
            ["--parsable", "--resolve", "--terminate", "_smb._tcp"],
            "Discover mDNS-advertised SMB services",
            cancellationToken);

        if (avahiResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(avahiResult.StandardOutput))
        {
            MergeMachines(machines, ParseAvahiBrowseOutput(avahiResult.StandardOutput));
            notes.Add("Read mDNS/Bonjour `_smb._tcp` advertisements with `avahi-browse`.");
        }
        else
        {
            notes.Add(DescribeMissingOrFailedCommand("avahi-browse", avahiResult));
        }

        var findSmbResult = await RunCommandAsync(
            "findsmb",
            [],
            "Discover SMB machines with NetBIOS broadcast",
            cancellationToken);

        if (findSmbResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(findSmbResult.StandardOutput))
        {
            MergeMachines(machines, ParseFindSmbOutput(findSmbResult.StandardOutput));
            notes.Add("Scanned the local subnet with `findsmb`.");
        }
        else
        {
            notes.Add(DescribeMissingOrFailedCommand("findsmb", findSmbResult));
        }

        var smbTreeResult = await RunCommandAsync(
            "smbtree",
            ["-S", "-N"],
            "Discover SMB servers with smbtree",
            cancellationToken);

        if (smbTreeResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(smbTreeResult.StandardOutput))
        {
            MergeMachines(machines, ParseSmbTreeServersOutput(smbTreeResult.StandardOutput));
            notes.Add("Augmented discovery with `smbtree -S -N`.");
        }
        else
        {
            notes.Add(DescribeMissingOrFailedCommand("smbtree", smbTreeResult));
        }

        var orderedMachines = machines.Values
            .OrderBy(machine => machine.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var statusMessage = orderedMachines.Length > 0
            ? $"Discovered {orderedMachines.Length} SMB-capable machine(s) on the network."
            : "No SMB-capable machines were discovered from this LMS host yet.";

        return new NetworkShareMachineDiscoveryResult(
            orderedMachines,
            statusMessage,
            notes,
            NetworkShareDiscoveryScope.Lan,
            tailscalePeerStatusReader.HasActiveInterface());
    }

    private async Task<NetworkShareMachineDiscoveryResult> DiscoverTailnetMachinesAsync(
        CancellationToken cancellationToken)
    {
        var tailnetStatus = await tailscalePeerStatusReader.ReadAsync(cancellationToken);
        if (!tailnetStatus.IsReady)
        {
            return tailnetStatus.Availability switch
            {
                TailscalePeerStatusAvailability.NoInterface => new NetworkShareMachineDiscoveryResult(
                    [],
                    "Tailnet share discovery is unavailable because this LMS host does not currently have an active Tailscale interface.",
                    ["Bring the LMS server onto the tailnet first, then search the tailnet again."],
                    NetworkShareDiscoveryScope.Tailnet),
                TailscalePeerStatusAvailability.CliMissing => new NetworkShareMachineDiscoveryResult(
                    [],
                    "Tailnet share discovery is unavailable because `tailscale` is not installed on this LMS host.",
                    ["Tailnet share discovery becomes available automatically when the LMS server has the Tailscale CLI installed and logged in."],
                    NetworkShareDiscoveryScope.Tailnet),
                _ => new NetworkShareMachineDiscoveryResult(
                    [],
                    $"Tailnet share discovery failed: {tailnetStatus.FailureMessage}",
                    ["Tailnet share discovery reads `tailscale status --json` and lists online peers as SMB browse candidates."],
                    NetworkShareDiscoveryScope.Tailnet,
                    true)
            };
        }

        var onlinePeers = tailnetStatus.Peers
            .Where(peer => peer.Online || peer.Active)
            .ToArray();

        if (onlinePeers.Length == 0)
        {
            return new NetworkShareMachineDiscoveryResult(
                [],
                "Tailscale is installed, but this LMS host does not currently see any online tailnet peers with IPv4 addresses.",
                ["Tailnet share discovery only lists peers that Tailscale currently reports as online or active."],
                NetworkShareDiscoveryScope.Tailnet,
                true);
        }

        var machines = await BuildTailnetMachinesAsync(onlinePeers, cancellationToken);

        return new NetworkShareMachineDiscoveryResult(
            machines,
            $"Discovered {machines.Count} tailnet peer(s) that LMS can interrogate for SMB shares.",
            ["Tailnet share discovery uses `tailscale status --json` to list online peers, then the normal share browse step interrogates the selected peer for SMB shares."],
            NetworkShareDiscoveryScope.Tailnet,
            true);
    }

    private async Task<IReadOnlyList<NetworkShareMachine>> BuildTailnetMachinesAsync(
        IReadOnlyList<TailnetPeer> peers,
        CancellationToken cancellationToken)
    {
        var resolverThrottle = new SemaphoreSlim(4);
        var machineTasks = peers.Select(async peer =>
        {
            var displayName = peer.DisplayName;

            if (peer.UsesGenericFallback)
            {
                await resolverThrottle.WaitAsync(cancellationToken);
                try
                {
                    var resolvedName = await TryResolveTailnetEndpointHostNameAsync(peer, cancellationToken)
                        ?? await TryResolveTailnetServerNameAsync(peer, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(resolvedName))
                    {
                        displayName = resolvedName;
                    }
                }
                finally
                {
                    resolverThrottle.Release();
                }
            }

            return new NetworkShareMachine(
                CreateStableId("tailnet-machine", $"{peer.Target}|{peer.IpAddress}"),
                displayName,
                peer.Target,
                peer.IpAddress,
                null,
                "Tailscale tailnet",
                peer.Platform,
                string.IsNullOrWhiteSpace(peer.OwnerLabel) ? null : $"Owner {peer.OwnerLabel}");
        });

        var machines = await Task.WhenAll(machineTasks);
        return machines
            .OrderBy(machine => machine.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(machine => machine.Target, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<string?> TryResolveTailnetEndpointHostNameAsync(
        TailnetPeer peer,
        CancellationToken cancellationToken)
    {
        var pingResult = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "tailscale",
                ["ping", "-c", "1", "--timeout", "2s", peer.IpAddress],
                RequiresSudo: false,
                Timeout: TimeSpan.FromSeconds(3),
                $"Resolve direct endpoint for {peer.IpAddress}")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        var endpointIp = TryParseTailscalePingEndpointAddress(pingResult.StandardOutput)
            ?? TryParseTailscalePingEndpointAddress(pingResult.StandardError);
        if (string.IsNullOrWhiteSpace(endpointIp))
        {
            return null;
        }

        var getentResult = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "getent",
                ["hosts", endpointIp],
                RequiresSudo: false,
                Timeout: TimeSpan.FromSeconds(2),
                $"Resolve hostname for {endpointIp}")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        var resolvedName = TryParseGetentHostName(getentResult.StandardOutput);
        if (!string.IsNullOrWhiteSpace(resolvedName))
        {
            return resolvedName;
        }

        var nmbLookupResult = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "nmblookup",
                ["-A", endpointIp],
                RequiresSudo: false,
                Timeout: TimeSpan.FromSeconds(3),
                $"Resolve NetBIOS hostname for {endpointIp}")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        return TryParseNetbiosHostName(nmbLookupResult.StandardOutput);
    }

    private async Task<string?> TryResolveTailnetServerNameAsync(
        TailnetPeer peer,
        CancellationToken cancellationToken)
    {
        var target = string.IsNullOrWhiteSpace(peer.IpAddress)
            ? peer.Target
            : peer.IpAddress;

        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "smbclient",
                ["-g", "-L", target, "-N", "-E"],
                RequiresSudo: false,
                Timeout: TimeSpan.FromSeconds(2),
                $"Resolve SMB server name for {target}")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        var browseOutput = CombineSmbClientBrowseOutput(result);
        if (string.IsNullOrWhiteSpace(browseOutput))
        {
            return null;
        }

        var parsed = ParseSmbClientBrowseOutput(target, browseOutput, usedAuthentication: false);
        return string.Equals(parsed.ResolvedName, target, StringComparison.OrdinalIgnoreCase) ||
               string.IsNullOrWhiteSpace(parsed.ResolvedName)
            ? null
            : parsed.ResolvedName;
    }

    internal static string? TryParseTailscalePingEndpointAddress(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var match = TailscalePingViaPattern.Match(output);
        return match.Success ? match.Groups["ip"].Value.Trim() : null;
    }

    internal static string? TryParseGetentHostName(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var segments = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length < 2)
            {
                continue;
            }

            var candidate = TailnetPeerNameFormatter.NormalizeFriendlyName(segments[1]);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static string? TryParseNetbiosHostName(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        string? fallback = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var markerIndex = line.IndexOf('<');
            if (markerIndex <= 0)
            {
                continue;
            }

            var candidate = line[..markerIndex].Trim();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (line.Contains("<00>", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("<GROUP>", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            fallback ??= candidate;
        }

        return fallback;
    }

    public async Task<RemoteShareBrowseResult> BrowseRemoteSharesAsync(
        string target,
        string? userName,
        string? password,
        string? domain,
        CancellationToken cancellationToken = default)
    {
        var normalizedTarget = target.Trim();
        var useAuthentication = !string.IsNullOrWhiteSpace(userName) ||
                                !string.IsNullOrWhiteSpace(password) ||
                                !string.IsNullOrWhiteSpace(domain);

        var authFilePath = useAuthentication
            ? await WriteTemporaryCredentialsFileAsync(userName, password, domain, cancellationToken)
            : null;

        try
        {
            var arguments = new List<string> { "-g", "-L", normalizedTarget, "-E" };
            if (!string.IsNullOrWhiteSpace(authFilePath))
            {
                arguments.Add("-A");
                arguments.Add(authFilePath);
            }
            else
            {
                arguments.Add("-N");
            }

            var result = await RunCommandAsync(
                "smbclient",
                arguments,
                $"Browse shares on {normalizedTarget}",
                cancellationToken);

            var browseOutput = CombineSmbClientBrowseOutput(result);
            if (!string.IsNullOrWhiteSpace(browseOutput))
            {
                var parsed = ParseSmbClientBrowseOutput(normalizedTarget, browseOutput, useAuthentication);
                if (result.ExitCode == 0 || parsed.Shares.Count > 0)
                {
                    var notes = parsed.Notes.ToList();
                    if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.StandardError) && parsed.Shares.Count == 0)
                    {
                        notes.Add(ExtractFailureMessage(result));
                    }

                    return parsed with
                    {
                        StatusMessage = parsed.Shares.Count > 0
                            ? $"Loaded {parsed.Shares.Count} share(s) from {parsed.ResolvedName}."
                            : $"Connected to {parsed.ResolvedName}, but it did not return any mountable shares.",
                        Notes = notes
                    };
                }
            }

            var failureMessage = ExtractFailureMessage(result);
            if (IsCommandUnavailable(result))
            {
                return new RemoteShareBrowseResult(
                    normalizedTarget,
                    normalizedTarget,
                    null,
                    useAuthentication,
                    false,
                    "Share browsing is unavailable because `smbclient` is not installed on this LMS host.",
                    Array.Empty<RemoteSambaShare>(),
                    [failureMessage]);
            }

            var requiresAuthentication = !useAuthentication && LooksLikeAuthenticationFailure(failureMessage);

            return new RemoteShareBrowseResult(
                normalizedTarget,
                normalizedTarget,
                null,
                useAuthentication,
                requiresAuthentication,
                requiresAuthentication
                    ? $"Anonymous browse failed for {normalizedTarget}. Credentials are probably required."
                    : $"Failed to browse shares on {normalizedTarget}.",
                Array.Empty<RemoteSambaShare>(),
                [failureMessage]);
        }
        finally
        {
            DeleteIfPresent(authFilePath);
        }
    }

    private static string CombineSmbClientBrowseOutput(LinuxCommandResult result)
    {
        if (string.IsNullOrWhiteSpace(result.StandardError))
        {
            return result.StandardOutput;
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return result.StandardError;
        }

        return string.Join(Environment.NewLine, result.StandardOutput, result.StandardError);
    }

    internal static IReadOnlyList<NetworkShareMachine> ParseFindSmbOutput(string output)
    {
        var machines = new List<NetworkShareMachine>();

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = rawLine.Trim();
            var match = FindSmbLinePattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var ipAddress = match.Groups["ip"].Value.Trim();
            var machineName = match.Groups["name"].Value.Trim();
            var bracketMatches = BracketValuePattern.Matches(match.Groups["rest"].Value);

            string? workgroup = null;
            string? operatingSystem = null;
            string? serverVersion = null;

            if (bracketMatches.Count > 0)
            {
                workgroup = bracketMatches[0].Groups["value"].Value.Trim();
            }

            if (bracketMatches.Count > 1)
            {
                operatingSystem = bracketMatches[1].Groups["value"].Value.Trim();
            }

            if (bracketMatches.Count > 2)
            {
                serverVersion = bracketMatches[2].Groups["value"].Value.Trim();
            }

            machines.Add(new NetworkShareMachine(
                CreateStableId("network-machine", $"{machineName}|{ipAddress}"),
                machineName,
                machineName,
                ipAddress,
                NullIfWhiteSpace(workgroup),
                "findsmb",
                NullIfWhiteSpace(operatingSystem),
                NullIfWhiteSpace(serverVersion)));
        }

        return machines;
    }

    internal static IReadOnlyList<NetworkShareMachine> ParseSmbTreeServersOutput(string output)
    {
        var machines = new List<NetworkShareMachine>();
        string? currentWorkgroup = null;

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var target = line.TrimStart('\\').Trim();
                machines.Add(new NetworkShareMachine(
                    CreateStableId("network-machine", target),
                    target,
                    target,
                    null,
                    currentWorkgroup,
                    "smbtree",
                    null,
                    null));
                continue;
            }

            currentWorkgroup = line;
        }

        return machines;
    }

    internal static IReadOnlyList<NetworkShareMachine> ParseAvahiBrowseOutput(string output)
    {
        var machines = new List<NetworkShareMachine>();

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("=;", StringComparison.Ordinal))
            {
                continue;
            }

            var segments = line.Split(';', StringSplitOptions.TrimEntries);
            if (segments.Length < 9)
            {
                continue;
            }

            var serviceName = DecodeAvahiValue(segments[3]);
            var hostName = DecodeAvahiValue(segments[6]).TrimEnd('.');
            var address = DecodeAvahiValue(segments[7]);
            var port = DecodeAvahiValue(segments[8]);
            var target = string.IsNullOrWhiteSpace(hostName) ? address : hostName;
            if (string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            machines.Add(new NetworkShareMachine(
                CreateStableId("network-machine", $"{target}|{address}"),
                string.IsNullOrWhiteSpace(serviceName) ? target : serviceName,
                target,
                NullIfWhiteSpace(address),
                null,
                "mDNS _smb._tcp",
                null,
                string.IsNullOrWhiteSpace(port) ? null : $"TCP {port}"));
        }

        return machines;
    }

    internal static RemoteShareBrowseResult ParseSmbClientBrowseOutput(
        string target,
        string output,
        bool usedAuthentication)
    {
        var shares = new Dictionary<string, RemoteSambaShare>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        var resolvedName = target;

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var segments = line.Split('|', StringSplitOptions.TrimEntries);
            if (segments.Length < 2)
            {
                continue;
            }

            if (segments[0].Equals("Server", StringComparison.OrdinalIgnoreCase) && segments.Length >= 2)
            {
                resolvedName = segments[1];
                continue;
            }

            if (segments[0].Equals("Workgroup", StringComparison.OrdinalIgnoreCase))
            {
                if (segments.Length >= 3 && !string.IsNullOrWhiteSpace(segments[2]))
                {
                    notes.Add($"Workgroup `{segments[1]}` master browser is `{segments[2]}`.");
                }

                continue;
            }

            if (TryParseShareEntry(segments, out var share))
            {
                shares[share.Name] = share;
            }
        }

        return new RemoteShareBrowseResult(
            target,
            resolvedName,
            null,
            usedAuthentication,
            false,
            shares.Count > 0 ? $"Loaded {shares.Count} share(s)." : "No shares returned.",
            shares.Values
                .OrderBy(share => share.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            notes);
    }

    private async Task<LinuxCommandResult> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        CancellationToken cancellationToken)
    {
        return await commandRunner.RunAsync(
            new LinuxCommandRequest(fileName, arguments, RequiresSudo: false, Timeout: TimeSpan.FromSeconds(15), description)
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);
    }

    private async Task<string> WriteTemporaryCredentialsFileAsync(
        string? userName,
        string? password,
        string? domain,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = Path.Combine(Path.GetTempPath(), $"lms-smb-auth-{Guid.NewGuid():N}.conf");
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(userName))
        {
            builder.AppendLine($"username = {userName.Trim()}");
        }

        builder.AppendLine($"password = {password?.Trim() ?? string.Empty}");

        if (!string.IsNullOrWhiteSpace(domain))
        {
            builder.AppendLine($"domain = {domain.Trim()}");
        }

        await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken);
        SetUnixFileModeIfSupported(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private static bool TryParseShareEntry(string[] segments, out RemoteSambaShare share)
    {
        var knownTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Disk",
            "Printer",
            "IPC",
            "IPC$",
            "Device"
        };

        string? shareType = null;
        string? shareName = null;
        string comment = string.Empty;

        if (knownTypes.Contains(segments[0]))
        {
            shareType = segments[0];
            shareName = segments.ElementAtOrDefault(1);
            comment = segments.ElementAtOrDefault(2) ?? string.Empty;
        }
        else if (segments.Length >= 2 && knownTypes.Contains(segments[1]))
        {
            shareName = segments[0];
            shareType = segments[1];
            comment = segments.ElementAtOrDefault(2) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(shareType) || string.IsNullOrWhiteSpace(shareName))
        {
            share = default!;
            return false;
        }

        var isSpecial = shareName.EndsWith("$", StringComparison.Ordinal) ||
                        shareType.Equals("IPC", StringComparison.OrdinalIgnoreCase) ||
                        shareType.Equals("Printer", StringComparison.OrdinalIgnoreCase) ||
                        shareType.Equals("Device", StringComparison.OrdinalIgnoreCase);

        share = new RemoteSambaShare(
            shareName,
            shareType,
            comment,
            shareType.Equals("Disk", StringComparison.OrdinalIgnoreCase) && !isSpecial,
            isSpecial);
        return true;
    }

    private static void MergeMachines(
        IDictionary<string, NetworkShareMachine> destination,
        IReadOnlyList<NetworkShareMachine> source)
    {
        foreach (var machine in source)
        {
            var existingKey = FindMatchingMachineKey(destination, machine);
            if (existingKey is not null && destination.TryGetValue(existingKey, out var existing))
            {
                destination[existingKey] = existing with
                {
                    IpAddress = existing.IpAddress ?? machine.IpAddress,
                    Workgroup = existing.Workgroup ?? machine.Workgroup,
                    OperatingSystem = existing.OperatingSystem ?? machine.OperatingSystem,
                    ServerVersion = existing.ServerVersion ?? machine.ServerVersion,
                    DiscoveryMethod = existing.DiscoveryMethod.Equals(machine.DiscoveryMethod, StringComparison.OrdinalIgnoreCase)
                        ? existing.DiscoveryMethod
                        : $"{existing.DiscoveryMethod}, {machine.DiscoveryMethod}"
                };
                continue;
            }

            destination[machine.Target] = machine;
        }
    }

    private static string? FindMatchingMachineKey(
        IDictionary<string, NetworkShareMachine> destination,
        NetworkShareMachine candidate)
    {
        if (destination.ContainsKey(candidate.Target))
        {
            return candidate.Target;
        }

        if (string.IsNullOrWhiteSpace(candidate.IpAddress))
        {
            return null;
        }

        return destination
            .Where(item => item.Value.IpAddress?.Equals(candidate.IpAddress, StringComparison.OrdinalIgnoreCase) == true)
            .Select(item => item.Key)
            .FirstOrDefault();
    }

    private static string DecodeAvahiValue(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value.Trim();
        }

        var builder = new StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' &&
                index + 3 < value.Length &&
                char.IsDigit(value[index + 1]) &&
                char.IsDigit(value[index + 2]) &&
                char.IsDigit(value[index + 3]) &&
                int.TryParse(value.AsSpan(index + 1, 3), out var characterCode))
            {
                builder.Append((char)characterCode);
                index += 3;
                continue;
            }

            builder.Append(value[index]);
        }

        return builder.ToString().Trim();
    }

    private static string DescribeMissingOrFailedCommand(string commandName, LinuxCommandResult result)
    {
        if (IsCommandUnavailable(result))
        {
            return $"`{commandName}` is not installed on this LMS host.";
        }

        if (LooksLikeCommandCrash(result))
        {
            return $"`{commandName}` crashed on this LMS host, so LMS ignored that probe and continued with the other SMB discovery paths.";
        }

        if (result.ExitCode == 0)
        {
            return $"`{commandName}` ran but did not return any SMB machines.";
        }

        return $"`{commandName}` did not return usable results: {ExtractFailureMessage(result)}";
    }

    private static string ExtractFailureMessage(LinuxCommandResult result)
    {
        var message = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();

        if (string.IsNullOrWhiteSpace(message))
        {
            return $"Command failed with exit code {result.ExitCode}.";
        }

        if (LooksLikeCommandCrash(result))
        {
            return "The Samba client tool crashed locally while probing the network.";
        }

        var firstLine = message
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return $"Command failed with exit code {result.ExitCode}.";
        }

        return firstLine.Length <= 220
            ? firstLine
            : $"{firstLine[..217]}...";
    }

    private static bool IsCommandUnavailable(LinuxCommandResult result) =>
        result.ExitCode is 127 or -1 ||
        result.StandardError.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        result.StandardError.Contains("No such file", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeCommandCrash(LinuxCommandResult result)
    {
        var combinedOutput = $"{result.StandardError}\n{result.StandardOutput}";
        return result.ExitCode == 139 ||
               combinedOutput.Contains("Segmentation fault", StringComparison.OrdinalIgnoreCase) ||
               combinedOutput.Contains("INTERNAL ERROR:", StringComparison.OrdinalIgnoreCase) ||
               combinedOutput.Contains("PANIC (pid", StringComparison.OrdinalIgnoreCase) ||
               combinedOutput.Contains("BACKTRACE:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeAuthenticationFailure(string message) =>
        message.Contains("NT_STATUS_ACCESS_DENIED", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("NT_STATUS_LOGON_FAILURE", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("session setup failed", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("anonymous", StringComparison.OrdinalIgnoreCase);

    private static Guid CreateStableId(string scope, string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}:{value.Trim().ToLowerInvariant()}"));
        return new Guid(bytes[..16]);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void DeleteIfPresent(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void SetUnixFileModeIfSupported(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }
}
