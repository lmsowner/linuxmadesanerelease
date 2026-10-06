// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.Infrastructure;
using LinuxMadeSane.Application.Contracts.HomeLab;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.Monitoring;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Services.Infrastructure;

public sealed class InfrastructureDiagnosticsService(
    ILinuxCommandRunner runner, IPackageManagementService packages, IServiceManagementService services,
    LinuxMadeSaneDbContext database, IManagedHostStore hosts, IFirewallManagementService firewall,
    IEdgeGatewayStore gateway, IStorageDiscoveryService storage, IDockerInventoryReader docker,
    ILocalSystemMonitorService monitor) : IInfrastructureDiagnosticsService
{
    internal static readonly IReadOnlyDictionary<string, string[]> FeaturePackages = new Dictionary<string, string[]>
    {
        ["SMART"] = ["smartmontools"], ["DHCP"] = ["kea-dhcp4-server"],
        ["Backup"] = ["restic"], ["UPS"] = ["nut-client", "nut-server"], ["Discovery"] = ["fping"]
    };
    private static readonly SemaphoreSlim InventoryGate = new(1, 1);

    internal Task<LinuxCommandResult> Run(string executable, string[] args, bool sudo = false,
        int seconds = 10, CancellationToken token = default) => runner.RunAsync(new(executable, args, sudo,
            TimeSpan.FromSeconds(seconds), $"Infrastructure: {executable}") { IsOptionalExternalTool = !sudo }, false, token);

    public async Task<FeaturePackageStatus> GetPackageStatusAsync(string feature, CancellationToken cancellationToken = default)
    {
        if (!FeaturePackages.TryGetValue(feature, out var names)) throw new ArgumentException("Unknown feature.");
        if (!OperatingSystem.IsLinux()) return new(feature, names, false, false, "This feature requires a Linux host.");
        var os = File.Exists("/etc/os-release") ? await File.ReadAllTextAsync("/etc/os-release", cancellationToken) : "";
        var apt = Regex.IsMatch(os, @"(?m)^(ID|ID_LIKE)=[""']?(ubuntu|debian)\b") ||
            Regex.IsMatch(os, @"(?m)^ID_LIKE=.*\b(ubuntu|debian)\b");
        var states = await packages.InspectAsync(names, cancellationToken);
        var installed = states.Count == names.Length && states.All(item => item.IsInstalled);
        return new(feature, names, installed, apt, installed ? "Required packages are installed." :
            apt ? $"Install {string.Join(", ", names)} using this host's APT package manager. Existing configuration will be preserved." :
            "Automatic installation currently supports Debian and Ubuntu family hosts. Install the listed distribution packages manually.");
    }

    public async Task InstallPackagesAsync(string feature, CancellationToken cancellationToken = default)
    {
        var status = await GetPackageStatusAsync(feature, cancellationToken);
        if (status.Installed) return;
        if (!status.CanInstall) throw new InvalidOperationException(status.Explanation);
        // Debian packages can start daemons from their post-install scripts. A new DHCP
        // server must stay inactive until a validated configuration is deliberately enabled.
        var units = feature == "DHCP" ? new[] { "kea-dhcp4-server" } :
            feature == "UPS" ? new[] { "nut-server", "nut-monitor", "nut-driver", "nut-driver-enumerator" } : [];
        var before = await services.InspectAsync(units, cancellationToken);
        var masked = new List<string>();
        try
        {
            foreach (var unit in before.Where(unit => !unit.IsMasked && !unit.IsActive))
            {
                var mask = await services.ApplyActionsAsync([new(ServiceActionKind.Mask, unit.Name, "Prevent package auto-start before configuration", false, "") { RuntimeOnly = true }], false, cancellationToken);
                if (mask.Any(log => log.Level == OperationLogLevel.Error)) throw new InvalidOperationException("Could not prevent automatic service start; installation was not attempted.");
                masked.Add(unit.Name);
            }
            var logs = await packages.ApplyActionsAsync(status.Packages.Select(name => new PackageAction(
                PackageActionKind.Install, name, $"Enable {feature}", false, "")).ToArray(), false, cancellationToken);
            foreach (var unit in before.Where(unit => !unit.IsActive && !unit.IsEnabled))
            {
                var disable = await services.ApplyActionsAsync([new(ServiceActionKind.Disable, unit.Name, "Await explicit feature configuration", false, "")], false, cancellationToken);
                if (disable.Any(log => log.Level == OperationLogLevel.Error)) throw new InvalidOperationException("Packages installed, but could not prevent startup on boot. Review service " + unit.Name + ".");
            }
            if (logs.Any(log => log.Level == OperationLogLevel.Error) || !(await GetPackageStatusAsync(feature, cancellationToken)).Installed)
                throw new InvalidOperationException("Installation failed: " + string.Join("; ", logs.Where(log => log.Level == OperationLogLevel.Error)
                    .Select(log => log.Message + " " + log.StandardError)));
        }
        finally
        {
            foreach (var unit in masked)
            {
                var unmask = await services.ApplyActionsAsync([new(ServiceActionKind.Unmask, unit, "Restore pre-install runtime mask state", false, "") { RuntimeOnly = true }], false, CancellationToken.None);
                if (unmask.Any(log => log.Level == OperationLogLevel.Error)) throw new InvalidOperationException("Could not remove the temporary runtime mask for " + unit + ". Review its service state.");
            }
        }
    }

    public async Task<DeviceInventory> GetCachedDevicesAsync(CancellationToken cancellationToken = default)
    {
        await InventoryGate.WaitAsync(cancellationToken);
        try
        {
            var row = await database.InfrastructureStates.FindAsync(["devices"], cancellationToken);
            return new(row is null ? [] : JsonSerializer.Deserialize<List<NetworkDevice>>(row.Json) ?? [],
                ["Cached observations from Network → Devices. These do not establish whether a device uses DHCP or a manually configured address."]);
        }
        finally { InventoryGate.Release(); }
    }

    public async Task<DeviceInventory> DiscoverDevicesAsync(CancellationToken cancellationToken = default)
    {
        await InventoryGate.WaitAsync(cancellationToken);
        try
        {
            var row = await database.InfrastructureStates.FindAsync(["devices"], cancellationToken);
            var devices = row is null ? [] : JsonSerializer.Deserialize<List<NetworkDevice>>(row.Json) ?? [];
            foreach (var item in devices) { item.State = "Not recently observed"; item.DhcpState = "Unknown"; item.Services.Clear(); item.ServiceUrls.Clear(); }
            var notices = new List<string> { "Passive discovery only. A cached address does not prove reachability; no ports or subnets are scanned." };
            if (!OperatingSystem.IsLinux()) return new(devices, ["Network discovery requires a Linux host."]);
            var interfaceDns = await ReadInterfaceDnsAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var neighbours = await Run("ip", ["-j", "neigh", "show"], token: cancellationToken);
            if (neighbours.ExitCode == 0)
            {
                foreach (var observation in ParseNeighbours(neighbours.StandardOutput)) Merge(devices, observation, now);
            }
            else notices.Add("Neighbour discovery unavailable: " + neighbours.StandardError);
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.IsIPv6LinkLocal) continue;
                    Merge(devices, new() { Hostname = Environment.MachineName, Addresses = [address.Address.ToString()],
                        Mac = NormalizeMac(nic.GetPhysicalAddress().ToString()), Interface = nic.Name,
                        Subnet = address.Address.AddressFamily == AddressFamily.InterNetwork ? KeaDhcpManagementService.NetworkPrefix(address.Address, address.PrefixLength) : $"{address.Address}/{address.PrefixLength}", State = "Local interface",
                        Sources = ["Local interface"] }, now);
                }
            }
            // mDNS advertisements are passive, optional and bounded. Do not install Avahi just to scan.
            var avahi = await Run("avahi-browse", ["--all", "--resolve", "--terminate", "--parsable"], seconds: 6, token: cancellationToken);
            if (avahi.ExitCode == 0)
                foreach (var line in avahi.StandardOutput.Split('\n'))
                {
                    var fields = line.Split(';');
                    if (fields.Length < 9 || fields[0] != "=" || !IPAddress.TryParse(fields[7], out _)) continue;
                    Merge(devices, new() { Addresses = [fields[7]], Hostname = fields[6], Interface = fields[1],
                        Services = [$"{fields[4]} :{fields[8]} ({fields[3]})"], Sources = ["mDNS"], State = "Advertised" }, now);
                }
            foreach (var host in await hosts.ListAsync(cancellationToken))
            {
                IPAddress[] addresses;
                try { addresses = await Dns.GetHostAddressesAsync(host.Hostname, cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
                catch (Exception e) when (e is SocketException or TimeoutException or ArgumentException) { continue; }
                // DNS results alone must never mark a host online.
                foreach (var address in addresses.Where(ip => !IPAddress.IsLoopback(ip)))
                    Merge(devices, new() { Hostname = host.Hostname, Addresses = [address.ToString()],
                        LmsHostId = host.Kind == ManagedHostKind.LmsHost ? host.Id : null,
                        Sources = ["Managed host"], Services = [$"SSH :{host.Port}", host.Kind == ManagedHostKind.LmsHost ? $"Registered LMS node · last connection test: {host.LastConnectionTestStatus}" : "Registered SSH host"] }, now);
            }
            // Read configured lease data only; DHCP does not need to be running in LMS.
            try
            {
                var configResult = await Run("cat", ["--", "/etc/kea/kea-dhcp4.conf"], true, token: cancellationToken);
                if (configResult.ExitCode == 0)
                {
                    var config = KeaDhcpManagementService.ParseConfiguration(configResult.StandardOutput);
                    var leasePath = config["Dhcp4"]?["lease-database"]?["name"]?.ToString() ?? "/var/lib/kea/kea-leases4.csv";
                    if (!string.IsNullOrWhiteSpace(leasePath) && Path.IsPathFullyQualified(leasePath) && config["Dhcp4"]?["lease-database"]?["type"]?.ToString() == "memfile")
                    {
                        var leases = await Run("cat", ["--", leasePath], true, token: cancellationToken);
                        if (leases.ExitCode == 0)
                            foreach (var lease in KeaDhcpManagementService.ParseLeases(leases.StandardOutput))
                                Merge(devices, new() { Addresses = [lease.Address], Mac = lease.Mac, Hostname = lease.Hostname,
                                    Sources = ["Kea lease"], DhcpState = lease.Active ? "Active lease" : "Expired lease" }, now);
                    }
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { notices.Add("Kea lease observations could not be imported: " + e.Message); }
            try
            {
                var containers = await docker.GetContainersAsync(cancellationToken);
                var local = devices.FirstOrDefault(device => device.Sources.Contains("Local interface"));
                if (local is not null && containers.Count > 0)
                {
                    local.Services.Add($"Docker: {containers.Count} containers ({containers.Count(item => item.Running)} running)");
                    foreach (var container in containers.Where(item => item.Running)) local.Services.Add($"{container.Name}: {container.Ports} · networks {container.Networks}");
                    local.Services = local.Services.Distinct().ToList();
                    // Existing deployed endpoint metadata supplies browser URLs. No guessed app paths.
                    var endpoints = await database.HomeLabServiceEndpoints.AsNoTracking().ToListAsync(cancellationToken);
                    var installations = await database.HomeLabInstallations.AsNoTracking().ToListAsync(cancellationToken);
                    var declaredInstallations = installations.Select(item =>
                    {
                        var app = HomeLabCatalog.VisibleApps.FirstOrDefault(app => app.Id == item.AppId);
                        return (item.Id, Entry: app is not null ? HomeLabService.ResolveBrowserEntry(item, app) : null);
                    }).Where(item => item.Entry is not null).ToDictionary(item => item.Id, item => item.Entry);
                    foreach (var endpoint in endpoints.Where(endpoint => declaredInstallations.ContainsKey(endpoint.InstallationId)))
                        if (Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
                            HomeLabBrowserUrl.Resolve(uri.GetLeftPart(UriPartial.Authority), declaredInstallations[endpoint.InstallationId]) == endpoint.Url)
                            local.ServiceUrls[endpoint.ServiceId] = endpoint.Url;
                }
            }
            catch (InvalidOperationException e) { notices.Add(e.Message); }
            foreach (var device in devices.Where(device => device.Mac.Length > 0)) device.Vendor = await ReadVendor(device.Mac, cancellationToken);
            // Use Linux's configured NSS/DNS resolver for reverse names, without
            // GetHostEntry's additional forward lookup or a 400ms cutoff.
            await Parallel.ForEachAsync(devices.Where(device => device.Addresses.Count > 0 &&
                    (string.IsNullOrWhiteSpace(device.Hostname) || IPAddress.TryParse(device.Hostname, out _)))
                .OrderBy(device => device.LastDnsLookupUtc).Take(64),
                new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
                (device, token) => new ValueTask(ResolveDeviceNameAsync(device, runner, token, interfaceDns.FirstOrDefault(dns => dns.Interface == device.Interface))));
            await Persist("devices", devices, cancellationToken);
            return new(devices.OrderBy(item => item.FriendlyName.Length > 0 ? item.FriendlyName : item.Hostname).ToArray(), notices)
            { InterfaceNetworks = KeaDhcpManagementService.ReadInterfaceNetworks(), InterfaceDns = interfaceDns };
        }
        finally { InventoryGate.Release(); }
    }

    public static void ValidateProbeSubnet(string listeningInterface, string subnet, IReadOnlyList<DhcpInterfaceNetwork> networks)
    {
        if (!KeaDhcpManagementService.IsPrivateSubnet(subnet) || !networks.Any(item => item.Interface == listeningInterface && item.Network == subnet))
            throw new InvalidOperationException("Select a private subnet actually configured on this host's interface. LMS will not scan an invented or remote range.");
        if (!int.TryParse(subnet.Split('/')[1], out var prefix) || prefix < 20)
            throw new InvalidOperationException("This subnet is larger than 4,096 addresses. Passive discovery remains available; a full scan would produce too much traffic.");
    }

    public async Task<DeviceInventory> ProbeSubnetAsync(string listeningInterface, string subnet, CancellationToken cancellationToken = default)
    {
        ValidateProbeSubnet(listeningInterface, subnet, KeaDhcpManagementService.ReadInterfaceNetworks());
        var package = await GetPackageStatusAsync("Discovery", cancellationToken);
        if (!package.Installed) throw new InvalidOperationException("Subnet discovery requires fping. Install it using the discovery package button first.");
        var probe = await Run("fping", ["-4", "-I", listeningInterface, "-a", "-q", "-r", "0", "-t", "250", "-i", "20", "-g", subnet],
            sudo: true, seconds: 150, token: cancellationToken);
        if (probe.ExitCode is not (0 or 1)) throw new InvalidOperationException("Subnet discovery failed: " + probe.StandardError);
        var inventory = await DiscoverDevicesAsync(cancellationToken);
        await InventoryGate.WaitAsync(cancellationToken);
        try
        {
            var row = await database.InfrastructureStates.FindAsync(["devices"], cancellationToken);
            var devices = row is null ? [] : JsonSerializer.Deserialize<List<NetworkDevice>>(row.Json) ?? [];
            var now = DateTimeOffset.UtcNow;
            foreach (var line in probe.StandardOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (IPAddress.TryParse(line, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork &&
                    KeaDhcpManagementService.NetworkPrefix(ip, int.Parse(subnet.Split('/')[1])) == subnet)
                    Merge(devices, new() { Addresses = [line], Interface = listeningInterface, Subnet = subnet, State = "Reachable", Sources = ["ICMP probe"] }, now);
            await Parallel.ForEachAsync(devices.Where(device => device.Interface == listeningInterface && device.Addresses.Count > 0 &&
                    (string.IsNullOrWhiteSpace(device.Hostname) || IPAddress.TryParse(device.Hostname, out _) || device.LastDnsLookupUtc is null || device.LastDnsLookupUtc < now.AddHours(-1))),
                new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken },
                (device, token) => new ValueTask(ResolveDeviceNameAsync(device, runner, token, inventory.InterfaceDns.FirstOrDefault(dns => dns.Interface == device.Interface))));
            await Persist("devices", devices, cancellationToken);
            inventory = inventory with { Devices = devices.OrderBy(item => item.Hostname).ToArray() };
        }
        finally { InventoryGate.Release(); }
        return inventory with { Notices = inventory.Notices.Append("Probed " + subnet + " on " + listeningInterface + " using ICMP only. IP, MAC and DNS names are cached. Devices that ignore ping may still appear in neighbours or leases.").ToArray() };
    }

    private async Task<IReadOnlyList<InterfaceDnsConfiguration>> ReadInterfaceDnsAsync(CancellationToken token)
    {
        var resolved = await Run("resolvectl", ["dns"], seconds: 3, token: token);
        var nm = await Run("nmcli", ["--terse", "--escape", "no", "--fields", "GENERAL.DEVICE,IP4.DNS,IP6.DNS,DHCP4.OPTION", "device", "show"], seconds: 3, token: token);
        return ParseInterfaceDns(resolved.ExitCode == 0 ? resolved.StandardOutput : "", nm.ExitCode == 0 ? nm.StandardOutput : "");
    }

    internal static IReadOnlyList<InterfaceDnsConfiguration> ParseInterfaceDns(string resolved, string networkManager)
    {
        var active = new Dictionary<string, InterfaceDnsConfiguration>(StringComparer.Ordinal);
        foreach (var line in resolved.Split('\n'))
        {
            var match = Regex.Match(line, @"^Link \d+ \(([^)]+)\):\s*(.*)$");
            if (match.Success) active[match.Groups[1].Value] = new(match.Groups[1].Value, DnsAddresses(match.Groups[2].Value), "systemd-resolved");
        }
        string current = "";
        var supplied = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var leases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in networkManager.Split('\n'))
        {
            var split = line.IndexOf(':'); if (split < 0) continue;
            var key = line[..split]; var value = line[(split + 1)..].Trim();
            if (key == "GENERAL.DEVICE") { current = value; continue; }
            if (current.Length == 0) continue;
            if (key.StartsWith("IP4.DNS[", StringComparison.Ordinal) || key.StartsWith("IP6.DNS[", StringComparison.Ordinal))
            {
                if (!active.TryGetValue(current, out var config)) config = new(current, [], "NetworkManager");
                if (config.Source == "NetworkManager") active[current] = config with { Servers = config.Servers.Concat(DnsAddresses(value)).Distinct().ToArray() };
            }
            if (key.StartsWith("DHCP4.OPTION[", StringComparison.Ordinal))
            {
                if (value.StartsWith("domain_name_servers = ", StringComparison.Ordinal)) supplied[current] = DnsAddresses(value[22..]).ToList();
                if (value.StartsWith("dhcp_server_identifier = ", StringComparison.Ordinal)) leases[current] = value[25..];
            }
        }
        return active.Values.Select(config => config with { DhcpServers = supplied.GetValueOrDefault(config.Interface) ?? [], DhcpServer = leases.GetValueOrDefault(config.Interface) ?? "" }).ToArray();
    }

    private static IReadOnlyList<string> DnsAddresses(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Where(value => IPAddress.TryParse(value.Split('#')[0], out _) || IPEndPoint.TryParse(value.Split('#')[0], out _)).Distinct().ToArray();

    internal static async Task ResolveDeviceNameAsync(NetworkDevice device, ILinuxCommandRunner commands, CancellationToken token, InterfaceDnsConfiguration? dns = null)
    {
        var addresses = device.Addresses.Select(value => IPAddress.TryParse(value, out var ip) ? ip : null)
            .Where(ip => ip is not null).OrderBy(ip => ip!.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).Distinct().Take(3).ToArray();
        var interfaceLookup = dns is { Source: "systemd-resolved", Servers.Count: > 0 };
        var resolver = interfaceLookup ? $"DNS servers {string.Join(", ", dns.Servers)} on {dns.Interface}" : "this host's configured resolver";
        device.NameLookupStatus = $"No reverse name returned by {resolver}.";
        try
        {
            foreach (var address in addresses)
            {
                var result = await commands.RunAsync(new(interfaceLookup ? "resolvectl" : "getent", interfaceLookup
                    ? ["--interface", dns!.Interface, "--protocol=dns", "--legend=no", "--cache=no", "query", address!.ToString()]
                    : ["hosts", address!.ToString()], false, TimeSpan.FromSeconds(3), $"Resolve device name using {resolver}") { IsOptionalExternalTool = true }, false, token);
                if (result.ExitCode == 127) { device.NameLookupStatus = interfaceLookup ? $"resolvectl is unavailable; could not query {resolver}." : "Linux getent is unavailable on this host; name lookup could not run."; break; }
                if (result.ExitCode == 124) { device.NameLookupStatus = $"Name lookup timed out querying {resolver}. Check DNS server and network access."; continue; }
                if (interfaceLookup && result.ExitCode != 0)
                {
                    device.NameLookupStatus = result.StandardError.Contains(".arpa' not found", StringComparison.Ordinal)
                        ? $"{resolver} returned no reverse DNS (PTR) record for {address}. A forward DNS name alone does not provide an IP-to-name lookup. On this DNS server, enable DHCP hostname registration or add a reverse/PTR record for the device."
                        : $"Could not resolve {address} using {resolver}: {result.StandardError.Trim()}";
                }
                var name = result.ExitCode == 0 ? ParseReverseName(interfaceLookup
                    ? result.StandardOutput.Replace($"{address}: ", $"{address} ", StringComparison.Ordinal) : result.StandardOutput, address) : null;
                if (name is null) continue;
                // Preserve DHCP/managed-host names; use the resolver to fill missing
                // names and refresh names previously obtained from this resolver.
                if (string.IsNullOrWhiteSpace(device.Hostname) || IPAddress.TryParse(device.Hostname, out _) || device.Sources.Contains("System resolver"))
                {
                    device.Hostname = name;
                    if (!device.Sources.Contains("System resolver")) device.Sources.Add("System resolver");
                }
                device.NameLookupStatus = $"Resolved {address} to {name} using {resolver}.";
                return;
            }
        }
        finally { device.LastDnsLookupUtc = DateTimeOffset.UtcNow; }
    }

    internal static string? ParseReverseName(string output, IPAddress address)
    {
        foreach (var line in output.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !IPAddress.TryParse(fields[0], out var found) || !found.Equals(address)) continue;
            var name = fields[1].TrimEnd('.');
            if (name.Length > 0 && !IPAddress.TryParse(name, out _)) return name;
        }
        return null;
    }

    public static IReadOnlyList<NetworkDevice> ParseNeighbours(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<NetworkDevice>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var address = Text(item, "dst");
            if (!IPAddress.TryParse(address, out _)) continue;
            var state = item.TryGetProperty("state", out var value) ? value.ToString() : "";
            var mac = NormalizeMac(Text(item, "lladdr"));
            // Failed ARP/ND attempts without an identity are not discovered devices.
            if (mac.Length == 0 && (state.Contains("FAILED", StringComparison.Ordinal) || state.Contains("INCOMPLETE", StringComparison.Ordinal))) continue;
            result.Add(new() { Addresses = [address], Mac = mac, Interface = Text(item, "dev"),
                Sources = ["Neighbour table"], State = state.Contains("REACHABLE", StringComparison.Ordinal) ? "Reachable" :
                    state.Contains("FAILED", StringComparison.Ordinal) ? "Unreachable" : "Cached neighbour" });
        }
        return result;
    }

    public static void Merge(List<NetworkDevice> devices, NetworkDevice observation, DateTimeOffset now)
    {
        observation.Mac = NormalizeMac(observation.Mac);
        var macMatch = observation.Mac.Length > 0 ? devices.FirstOrDefault(item => item.Mac == observation.Mac) : null;
        var addressMatch = devices.FirstOrDefault(item => item.Addresses.Intersect(observation.Addresses).Any() &&
            (observation.Interface.Length == 0 || item.Interface.Length == 0 || item.Interface == observation.Interface) &&
            (observation.Mac.Length == 0 || item.Mac.Length == 0 || item.Mac == observation.Mac));
        var existing = macMatch ?? addressMatch;
        // A lease reassignment must not merge two different MAC identities.
        if (observation.Mac.Length > 0)
            foreach (var other in devices.Where(item => item != existing && item.Mac.Length > 0 && item.Mac != observation.Mac))
                other.Addresses.RemoveAll(address => observation.Addresses.Contains(address));
        if (existing is null)
        {
            observation.FirstSeenUtc = observation.LastSeenUtc = now;
            devices.Add(observation); return;
        }
        existing.Addresses = existing.Addresses.Union(observation.Addresses).ToList();
        existing.Sources = existing.Sources.Union(observation.Sources).ToList();
        existing.Services = existing.Services.Union(observation.Services).ToList();
        if (observation.DhcpState != "Unknown") existing.DhcpState = observation.DhcpState;
        if (observation.Mac.Length > 0) existing.Mac = observation.Mac;
        if (observation.Hostname.Length > 0) existing.Hostname = observation.Hostname;
        if (observation.Interface.Length > 0) existing.Interface = observation.Interface;
        if (observation.Subnet.Length > 0) existing.Subnet = observation.Subnet;
        existing.LmsHostId ??= observation.LmsHostId;
        if (observation.State != "Not recently observed")
        {
            if (ObservationStrength(observation.State) >= ObservationStrength(existing.State)) existing.State = observation.State;
            existing.LastSeenUtc = now;
        }
    }

    private static int ObservationStrength(string state) => state switch
    { "Local interface" => 5, "Reachable" => 4, "Advertised" => 3, "Cached neighbour" => 2, "Unreachable" => 1, _ => 0 };

    public async Task SaveDeviceAsync(NetworkDevice device, CancellationToken cancellationToken = default)
    {
        await InventoryGate.WaitAsync(cancellationToken);
        try
        {
            var row = await database.InfrastructureStates.FindAsync(["devices"], cancellationToken);
            var devices = row is null ? [] : JsonSerializer.Deserialize<List<NetworkDevice>>(row.Json) ?? [];
            var existing = devices.Single(item => item.Id == device.Id);
            existing.FriendlyName = device.FriendlyName.Trim(); existing.Notes = device.Notes; existing.Tags = device.Tags;
            await Persist("devices", devices, cancellationToken);
        }
        finally { InventoryGate.Release(); }
    }

    public async Task WakeAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var row = await database.InfrastructureStates.FindAsync(["devices"], cancellationToken);
        var device = (JsonSerializer.Deserialize<List<NetworkDevice>>(row?.Json ?? "[]") ?? []).Single(item => item.Id == deviceId);
        var mac = device.Mac.Replace(":", "");
        if (!Regex.IsMatch(mac, "^[0-9A-F]{12}$")) throw new InvalidOperationException("This device has no usable MAC address.");
        // A single standard magic packet, never a subnet sweep.
        using var socket = new UdpClient { EnableBroadcast = true };
        var packet = Enumerable.Repeat((byte)255, 6).Concat(Enumerable.Range(0, 16).SelectMany(_ => Convert.FromHexString(mac))).ToArray();
        await socket.SendAsync(packet, new IPEndPoint(IPAddress.Broadcast, 9), cancellationToken);
    }

    public async Task<ExposureSnapshot> GetExposureAsync(CancellationToken cancellationToken = default)
    {
        var status = await firewall.GetStatusAsync(cancellationToken);
        var routes = await gateway.ListRoutesAsync(cancellationToken);
        var entries = new List<ExposureEntry>();
        var notices = new List<string> { "External accessibility could not be determined. Listener bindings and local firewall rules cannot establish router, NAT or upstream firewall behavior." };
        if (!status.IsInstalled) notices.Add("UFW is not installed. Listening services are still shown; other local or upstream firewall protection has not been determined.");
        else if (status.Warning is not null) notices.Add(status.Warning);
        // The existing process monitor includes loopback sockets and works without
        // UFW installed. Firewall's listener list deliberately excludes loopback.
        var processListeners = new List<(string Service, string Protocol, string Address, int Port)>();
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var sample = await monitor.CaptureAsync(new LocalSystemMonitorCaptureOptions(IncludeListeningPorts: true), cancellationToken);
                foreach (var process in sample.Processes)
                    foreach (var socket in process.ListeningPorts)
                        processListeners.Add((process.ProcessId == Environment.ProcessId ? "LMS" : process.Name, socket.Protocol.ToLowerInvariant(), socket.Address, socket.Port));
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
            { notices.Add("Socket ownership could not be read: " + e.Message); }
        }
        var listeners = processListeners.Distinct().ToList();
        foreach (var listener in status.ListeningPorts)
            if (!listeners.Any(item => item.Protocol == listener.Protocol && item.Port == listener.Port &&
                (item.Address == listener.Destination || listener.Destination == "any" && item.Address is "0.0.0.0" or "::" or "*")))
                listeners.Add((listener.Protocol.ToUpperInvariant() + " service", listener.Protocol, listener.Destination, listener.Port));
        foreach (var listener in listeners)
        {
            var matches = routes.Where(route => route.Enabled && route.TargetPort == listener.Port && IsLocalTarget(route.TargetHost)).ToArray();
            var reasoning = new List<string> { $"Observed {listener.Protocol} listening socket.",
                status.IsActive ? $"Local firewall is active; default incoming policy: {status.IncomingPolicy}." : "Local firewall protection is not confirmed active." };
            if (listener.Address is "0.0.0.0" or "::" or "[::]" or "*" or "any")
                reasoning.Add("Listening on all interfaces. This includes interfaces added later; it does not prove Internet access.");
            if (IPAddress.TryParse(listener.Address, out var binding) && IPAddress.IsLoopback(binding))
                reasoning.Add("Bound to loopback. Direct connections are limited to this host; proxies or SSH forwards may still provide access.");
            foreach (var rule in status.Rules.Where(rule => rule.IsEnabled && (rule.Destination == listener.Port.ToString() || rule.Destination.StartsWith(listener.Port + "/"))))
                reasoning.Add($"Local rule: {rule.Action}, source {rule.Source}. {rule.Comment}");
            if (matches.Length > 0) reasoning.Add("An enabled Edge Gateway route targets this local port. Direct listener access may also exist; upstream reachability is unverified.");
            entries.Add(new(matches.FirstOrDefault()?.DisplayName ?? listener.Service,
                FormatListener(listener.Address, listener.Port), string.Join(", ", matches.Select(route => route.Hostname + "." + route.DomainName)),
                matches.Length > 0 ? "Edge Gateway + local listener" : "Local listener", reasoning));
        }
        foreach (var route in routes.Where(route => route.Enabled && !entries.Any(entry => entry.External.Split(", ").Contains(route.Hostname + "." + route.DomainName))))
            entries.Add(new(route.DisplayName, $"{route.TargetHost}:{route.TargetPort}", route.Hostname + "." + route.DomainName,
                "Edge Gateway", ["Configured route. Route configuration alone does not verify upstream health or external reachability."]));
        try
        {
            foreach (var container in (await docker.GetContainersAsync(cancellationToken)).Where(item => item.Running))
                foreach (var mapping in container.Ports.Split(',', StringSplitOptions.TrimEntries).Where(mapping => mapping.Contains("->")))
                    entries.Add(new(container.Name, mapping, "", "Docker published port", [
                        "Docker publishes this port using its networking rules, which may bypass ordinary UFW incoming rules.",
                        "Docker networks: " + container.Networks,
                        "External accessibility could not be determined. Review the host, router and upstream firewall; publication is not proof of Internet access."]));
        }
        catch (InvalidOperationException e) { notices.Add(e.Message); }
        return new(entries, notices);
    }

    private static string FormatListener(string address, int port) => (address.Contains(':') && !address.StartsWith('[') ? "[" + address + "]" : address) + ":" + port;

    private static bool IsLocalTarget(string target) => target is "localhost" or "127.0.0.1" or "::1" ||
        NetworkInterface.GetAllNetworkInterfaces().SelectMany(nic => nic.GetIPProperties().UnicastAddresses).Any(address => address.Address.ToString() == target);

    public async Task<TimeDiagnostics> GetTimeAsync(CancellationToken cancellationToken = default)
    {
        var timedate = await Run("timedatectl", ["show", "--property=Timezone", "--property=NTPSynchronized", "--property=NTP"], token: cancellationToken);
        var units = await services.InspectAsync(["chrony", "chronyd", "systemd-timesyncd", "ntp", "ntpd", "ntpsec"], cancellationToken);
        var candidates = units.Where(unit => unit.IsActive || unit.IsEnabled).ToArray();
        var selected = candidates.FirstOrDefault(unit => unit.IsActive) ?? candidates.FirstOrDefault();
        var provider = selected?.Name ?? "Not identified";
        var source = "No provider diagnostics available.";
        var notices = new List<string>();
        if (selected is not null)
        {
            var detail = provider.StartsWith("chron") ? await Run("chronyc", ["tracking"], token: cancellationToken) :
                provider.StartsWith("systemd-timesyncd") ? await Run("timedatectl", ["timesync-status"], token: cancellationToken) :
                await Run("ntpq", ["-pn"], token: cancellationToken);
            source = detail.ExitCode == 0 ? detail.StandardOutput : "Could not read provider details: " + detail.StandardError;
            if (provider.StartsWith("chron"))
            {
                var sources = await Run("chronyc", ["sources", "-v"], token: cancellationToken);
                source += "\n" + (sources.ExitCode == 0 ? sources.StandardOutput : sources.StandardError);
                if (Regex.IsMatch(detail.StandardOutput, @"Leap status\s*:\s*(Not synchronised|Not synchronized)"))
                    notices.Add("Chrony reports that the clock is not synchronized.");
                var offset = Regex.Match(detail.StandardOutput, @"System time\s*:\s*([0-9.]+) seconds");
                if (offset.Success && double.TryParse(offset.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 1) notices.Add("Clock offset exceeds one second; inspect source reachability and synchronization.");
            }
        }
        if (candidates.Count(unit => unit.IsActive) > 1) notices.Add("Multiple time providers appear active. Check the host configuration; LMS will not replace them.");
        var synced = timedate.StandardOutput.Contains("NTPSynchronized=yes", StringComparison.Ordinal);
        if (!synced) notices.Add("Synchronization is not confirmed. Check the provider's source status and network connectivity.");
        if (selected?.IsActive != true) notices.Add("No active supported time service was found. Containers may share their host's clock.");
        var timezone = timedate.StandardOutput.Split('\n').FirstOrDefault(line => line.StartsWith("Timezone="))?[9..] ?? TimeZoneInfo.Local.Id;
        if (timezone != TimeZoneInfo.Local.Id) notices.Add("System timezone differs from the timezone cached by LMS. Check timezone settings and restart LMS if it changed while LMS was running.");
        var summary = ParseTimeSource(provider, source);
        return new(DateTimeOffset.Now, DateTimeOffset.UtcNow, timezone, provider, provider,
            selected?.ActiveState ?? "Unknown", synced ? "Synchronized" : "Not confirmed", source, notices)
        { ActiveSource = summary.Source, EstimatedOffset = summary.Offset, LastSynchronization = summary.LastSync };
    }

    public static (string Source, string Offset, string LastSync) ParseTimeSource(string provider, string details)
    {
        string Field(string name)
        {
            var match = Regex.Match(details, @"(?m)^\s*" + Regex.Escape(name) + @"\s*:\s*(.+)$");
            return match.Success ? match.Groups[1].Value.Trim() : "Not reported";
        }
        if (provider.StartsWith("chron", StringComparison.Ordinal))
            return (Field("Reference ID"), Field("System time"), Field("Ref time (UTC)"));
        if (provider.StartsWith("systemd-timesyncd", StringComparison.Ordinal))
            return (Field("Server"), Field("Offset"), "Not reported by this provider's status command");
        var selected = details.Split('\n').FirstOrDefault(line => line.TrimStart().StartsWith('*'));
        var fields = selected?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return fields is { Length: >= 9 } ? (fields[0].TrimStart('*'), fields[8] + " ms", "Not reported by ntpq") :
            ("No selected source reported", "Not reported", "Not reported");
    }

    public async Task ControlTimeServiceAsync(bool enable, CancellationToken cancellationToken = default)
    {
        var diagnostic = await GetTimeAsync(cancellationToken);
        if (diagnostic.Provider == "Not identified") throw new InvalidOperationException("No existing time provider identified. LMS will not install or replace one automatically.");
        var logs = await services.ApplyActionsAsync([new(enable ? ServiceActionKind.Enable : ServiceActionKind.Restart,
            diagnostic.Service, "Time diagnostics action", false, "")], false, cancellationToken);
        if (logs.Any(log => log.Level == OperationLogLevel.Error)) throw new InvalidOperationException(string.Join("; ", logs.Select(log => log.Message)));
    }

    public async Task<IReadOnlyList<SmartHealth>> GetSmartAsync(CancellationToken cancellationToken = default)
    {
        if (!(await GetPackageStatusAsync("SMART", cancellationToken)).Installed) return [];
        return await storage.ReadSmartAsync(cancellationToken);
    }

    public static SmartHealth ParseSmart(string device, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        bool? healthy = root.TryGetProperty("smart_status", out var status) && status.TryGetProperty("passed", out var passed) ? passed.GetBoolean() : null;
        var details = new Dictionary<string, string>();
        var warnings = new List<string>();
        int? temp = root.TryGetProperty("temperature", out var temperature) ? Number(temperature, "current") : null;
        long? hours = root.TryGetProperty("power_on_time", out var time) && time.TryGetProperty("hours", out var hour) && hour.TryGetInt64(out var h) ? h : null;
        int? wear = null;
        if (root.TryGetProperty("nvme_smart_health_information_log", out var nvme))
        {
            temp ??= Number(nvme, "temperature");
            wear = Number(nvme, "percentage_used");
            foreach (var key in new[] { "critical_warning", "available_spare", "percentage_used", "unsafe_shutdowns", "media_errors", "num_err_log_entries" })
                if (nvme.TryGetProperty(key, out var value)) details[key.Replace('_', ' ')] = value.ToString();
            if (Number(nvme, "critical_warning") is > 0) warnings.Add("NVMe reports a critical health warning.");
            if (Number(nvme, "media_errors") is > 0) warnings.Add("NVMe reports media/data integrity errors.");
        }
        if (root.TryGetProperty("ata_smart_attributes", out var attributes) && attributes.TryGetProperty("table", out var table))
            foreach (var attribute in table.EnumerateArray())
            {
                var id = Number(attribute, "id");
                if (id is not (5 or 197 or 198)) continue;
                if (!attribute.TryGetProperty("raw", out var raw) || !raw.TryGetProperty("value", out var value)) continue;
                var name = id == 5 ? "Reallocated sectors" : id == 197 ? "Pending sectors" : "Uncorrectable sectors";
                details[name] = value.ToString();
                if (value.TryGetInt64(out var count) && count > 0) warnings.Add($"{name}: {count}. Review the drive and backup status.");
            }
        if (healthy == false) warnings.Add("The drive reports failed SMART overall health. Protect your data and inspect the disk.");
        return new(device, healthy.HasValue || details.Count > 0, healthy, Text(root, "model_name"), temp, hours, wear,
            details, warnings, json, healthy.HasValue || details.Count > 0 ? "" : "SMART is not exposed by this device, or it is sleeping. This is common in VMs and containers.");
    }

    internal async Task Persist<T>(string key, T value, CancellationToken token)
    {
        var entity = await database.InfrastructureStates.FindAsync([key], token);
        if (entity is null) { entity = new() { Key = key }; database.InfrastructureStates.Add(entity); }
        entity.Json = JsonSerializer.Serialize(value); entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(token);
    }
    internal static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var item) ? item.ToString() : "";
    private static int? Number(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number) ? number : null;
    private static string NormalizeMac(string mac)
    {
        var hex = mac.Replace(":", "").Replace("-", "").ToUpperInvariant();
        return Regex.IsMatch(hex, "^[0-9A-F]{12}$") ? string.Join(":", Enumerable.Range(0, 6).Select(index => hex.Substring(index * 2, 2))) : "";
    }
    private static async Task<string> ReadVendor(string mac, CancellationToken token)
    {
        // Use a distribution-maintained OUI database if present, without installing or
        // sending device identifiers to an external lookup service.
        const string path = "/usr/share/ieee-data/oui.txt";
        if (!File.Exists(path)) return "Unknown (local OUI database unavailable)";
        var prefix = mac[..8].Replace(':', '-');
        using var reader = File.OpenText(path);
        while (await reader.ReadLineAsync(token) is { } line)
            if (line.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && line.Contains("(hex)"))
                return line[(line.IndexOf("(hex)", StringComparison.Ordinal) + 5)..].Trim();
        return "Unknown";
    }
}
