// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

internal sealed class TailscalePeerStatusReader(
    ILinuxCommandRunner commandRunner,
    Func<IReadOnlyList<TailscaleInterfaceInfo>>? interfaceProvider = null)
{
    private readonly Func<IReadOnlyList<TailscaleInterfaceInfo>> interfaceProvider = interfaceProvider ?? ReadLocalInterfaces;

    public bool HasActiveInterface() => HasActiveInterface(interfaceProvider());

    public async Task<TailscalePeerStatusSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!HasActiveInterface())
        {
            return new TailscalePeerStatusSnapshot(
                TailscalePeerStatusAvailability.NoInterface,
                [],
                "This LMS host does not currently have an active Tailscale interface.");
        }

        var commandResult = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "tailscale",
                ["status", "--json"],
                false,
                TimeSpan.FromSeconds(10),
                "List tailnet peers")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        if (commandResult.ExitCode == 127)
        {
            return new TailscalePeerStatusSnapshot(
                TailscalePeerStatusAvailability.CliMissing,
                [],
                "`tailscale` is not installed on this LMS host.");
        }

        if (commandResult.ExitCode != 0)
        {
            return new TailscalePeerStatusSnapshot(
                TailscalePeerStatusAvailability.CommandFailed,
                [],
                BuildFailureMessage(commandResult));
        }

        return new TailscalePeerStatusSnapshot(
            TailscalePeerStatusAvailability.Ready,
            ParseTailnetPeers(commandResult.StandardOutput),
            null);
    }

    internal static IReadOnlyList<TailnetPeer> ParseTailnetPeers(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var userDisplayNames = ParseUserDisplayNames(root);

        if (!root.TryGetProperty("Peer", out var peersElement))
        {
            return [];
        }

        var peers = new List<TailnetPeer>();
        var peerValues = peersElement.ValueKind switch
        {
            JsonValueKind.Object => peersElement.EnumerateObject().Select(item => item.Value),
            JsonValueKind.Array => peersElement.EnumerateArray(),
            _ => Enumerable.Empty<JsonElement>()
        };

        foreach (var peer in peerValues)
        {
            var ipAddress = ReadFirstIpv4(peer, "TailscaleIPs");
            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                continue;
            }

            var customName = ReadString(peer, "Name")
                ?? ReadString(peer, "ComputedName")
                ?? ReadString(peer, "DisplayName");
            var hostName = TailnetPeerNameFormatter.NormalizeDnsName(ReadString(peer, "HostName"));
            var dnsName = TailnetPeerNameFormatter.NormalizeDnsName(ReadString(peer, "DNSName"));
            var ownerLabel = ReadOwnerLabel(peer, userDisplayNames);
            var friendlyName = TailnetPeerNameFormatter.ResolveFriendlyName(customName, hostName, dnsName);
            var displayName = TailnetPeerNameFormatter.BuildDisplayName(
                customName,
                hostName,
                dnsName,
                ipAddress,
                ownerLabel,
                "Tailnet peer");
            var target = TailnetPeerNameFormatter.BuildTarget(hostName, dnsName, ipAddress);

            peers.Add(new TailnetPeer(
                displayName,
                target,
                ipAddress,
                NullIfWhiteSpace(ReadString(peer, "OS")),
                ownerLabel,
                string.IsNullOrWhiteSpace(friendlyName),
                ReadBoolean(peer, "Online"),
                ReadBoolean(peer, "Active")));
        }

        return peers
            .DistinctBy(peer => peer.IpAddress, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(peer => peer.Online || peer.Active)
            .ThenBy(peer => peer.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(peer => peer.Target, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool HasActiveInterface(IReadOnlyList<TailscaleInterfaceInfo> interfaces) =>
        interfaces.Any(networkInterface =>
            networkInterface.IsUp &&
            networkInterface.SupportsIpv4 &&
            networkInterface.Name.StartsWith("tailscale", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<TailscaleInterfaceInfo> ReadLocalInterfaces() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Select(networkInterface => new TailscaleInterfaceInfo(
                networkInterface.Name.Trim(),
                networkInterface.OperationalStatus == OperationalStatus.Up,
                networkInterface.Supports(NetworkInterfaceComponent.IPv4) &&
                networkInterface.GetIPProperties().UnicastAddresses.Any(address =>
                    address.Address.AddressFamily == AddressFamily.InterNetwork)))
            .ToArray();

    private static Dictionary<long, string> ParseUserDisplayNames(JsonElement root)
    {
        if (!root.TryGetProperty("User", out var usersElement) || usersElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var users = new Dictionary<long, string>();
        foreach (var user in usersElement.EnumerateObject())
        {
            if (!long.TryParse(user.Name, out var userId))
            {
                continue;
            }

            var displayName = NormalizeDnsName(ReadString(user.Value, "DisplayName"));
            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            users[userId] = displayName;
        }

        return users;
    }

    private static string? ReadOwnerLabel(JsonElement peer, IReadOnlyDictionary<long, string> userDisplayNames)
    {
        if (!peer.TryGetProperty("UserID", out var userIdElement) || userIdElement.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (!userIdElement.TryGetInt64(out var userId))
        {
            return null;
        }

        return userDisplayNames.TryGetValue(userId, out var ownerLabel)
            ? ownerLabel
            : null;
    }

    private static string? NormalizeDnsName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool ReadBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        property.GetBoolean();

    private static string? ReadFirstIpv4(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in property.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var candidate = entry.GetString();
            if (!string.IsNullOrWhiteSpace(candidate) &&
                candidate.Contains('.', StringComparison.Ordinal))
            {
                return candidate.Trim();
            }
        }

        return null;
    }

    private static string BuildFailureMessage(LinuxCommandResult commandResult)
    {
        var output = string.IsNullOrWhiteSpace(commandResult.StandardError)
            ? commandResult.StandardOutput
            : commandResult.StandardError;

        var firstLine = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(firstLine)
            ? $"Command exited with status {commandResult.ExitCode}."
            : firstLine;
    }
}

internal sealed record TailnetPeer(
    string DisplayName,
    string Target,
    string IpAddress,
    string? Platform,
    string? OwnerLabel,
    bool UsesGenericFallback,
    bool Online,
    bool Active);

internal sealed record TailscaleInterfaceInfo(
    string Name,
    bool IsUp,
    bool SupportsIpv4);

internal sealed record TailscalePeerStatusSnapshot(
    TailscalePeerStatusAvailability Availability,
    IReadOnlyList<TailnetPeer> Peers,
    string? FailureMessage)
{
    public bool IsReady => Availability == TailscalePeerStatusAvailability.Ready;
}

internal enum TailscalePeerStatusAvailability
{
    Ready = 0,
    NoInterface = 1,
    CliMissing = 2,
    CommandFailed = 3
}
