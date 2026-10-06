// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.Infrastructure;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services.Infrastructure;

public sealed class KeaDhcpManagementService(ILinuxCommandRunner runner, IServiceManagementService services)
    : IDhcpManagementService
{
    private const string Config = "/etc/kea/kea-dhcp4.conf";
    private const string Unit = "kea-dhcp4-server";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private Task<LinuxCommandResult> Command(string executable, string[] args, bool sudo, CancellationToken token,
        byte[]? input = null) => runner.RunAsync(new(executable, args, sudo, TimeSpan.FromSeconds(30), "Kea DHCP configuration")
        { StandardInputBytes = input }, false, token);

    private async Task<string> Read(CancellationToken token)
    {
        var result = await Command("python3", ["-c", "import pathlib,json; p=pathlib.Path('/etc/kea/kea-dhcp4.conf'); print(json.dumps(p.read_bytes().decode('utf-8') if p.exists() else ''))"], true, token);
        if (result.ExitCode != 0) throw new InvalidOperationException("Could not read Kea configuration: " + result.StandardError);
        return JsonSerializer.Deserialize<string>(result.StandardOutput) ?? "";
    }
    public async Task<DhcpWorkspace> GetAsync(CancellationToken cancellationToken = default)
    {
        var raw = await Read(cancellationToken);
        var state = (await services.InspectAsync([Unit], cancellationToken)).Single();
        var editor = new DhcpEditor { Enabled = state.IsActive, OriginalHash = Hash(raw) };
        var notices = new List<string>();
        var invocation = await Command("systemctl", ["show", Unit, "--property=ExecStart", "--value"], false, cancellationToken);
        JsonObject? document = null;
        bool readOnly = false;
        if (invocation.ExitCode == 0 && invocation.StandardOutput.Contains(" -c ", StringComparison.Ordinal) &&
            !invocation.StandardOutput.Contains(" -c " + Config, StringComparison.Ordinal))
        { readOnly = true; notices.Add("Kea uses a custom configuration path. LMS will not modify the default file for this service."); }
        try { document = ParseConfiguration(raw); }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        { readOnly = true; notices.Add("Existing configuration is preserved and read-only: " + e.Message); }
        if (document?["Dhcp4"] is JsonObject dhcp)
        {
            editor.Interface = dhcp["interfaces-config"]?["interfaces"]?.AsArray().FirstOrDefault()?.ToString() ?? "";
            var subnet = dhcp["subnet4"]?.AsArray().FirstOrDefault()?.AsObject();
            if (subnet is not null)
            {
                editor.SubnetId = subnet["id"]?.GetValue<uint>() ?? 1;
                editor.Subnet = subnet["subnet"]?.ToString() ?? "";
                editor.LeaseSeconds = subnet["valid-lifetime"]?.GetValue<int>() ?? dhcp["valid-lifetime"]?.GetValue<int>() ?? 86400;
                var pool = subnet["pools"]?.AsArray().FirstOrDefault()?["pool"]?.ToString().Split('-', 2, StringSplitOptions.TrimEntries);
                if (pool?.Length == 2) { editor.PoolStart = pool[0]; editor.PoolEnd = pool[1]; }
                editor.Gateway = Option(subnet, dhcp, "routers");
                editor.DnsServers = Option(subnet, dhcp, "domain-name-servers");
                editor.Reservations = subnet["reservations"]?.AsArray().OfType<JsonObject>()
                    .Where(value => value["hw-address"] is not null && value["ip-address"] is not null)
                    .Select(value => new DhcpReservation(value["hw-address"]!.ToString(), value["ip-address"]!.ToString(), value["hostname"]?.ToString() ?? "")).ToList() ?? [];
                if (subnet["pools"]?.AsArray().Count > 1) { readOnly = true; notices.Add("This subnet has multiple pools. The simple editor will not replace them."); }
                if (editor.Interface.Contains('*')) { readOnly = true; notices.Add("Wildcard listening is preserved. Select explicit interfaces in Kea before using the simple editor."); }
            }
            if (dhcp["shared-networks"] is JsonArray { Count: > 0 }) { readOnly = true; notices.Add("Shared-network configuration is preserved; the simple editor does not modify it."); }
        }
        var documentationConfiguration = IsDocumentationSubnet(editor.Subnet);
        if (documentationConfiguration)
        {
            notices.Add("Kea contains a documentation-only example network, not your LAN. Select a listening interface and enter your pool, gateway and DNS servers. Applying replaces the example subnet and its sample reservations; the original file is backed up.");
            editor.Subnet = editor.PoolStart = editor.PoolEnd = editor.Gateway = editor.DnsServers = "";
            editor.Reservations.Clear();
        }
        else if (raw.Length > 0) notices.Add("Existing Kea settings loaded. Select ‘Allow LMS to manage’ before applying changes.");
        var leasePath = document?["Dhcp4"]?["lease-database"]?["name"]?.ToString() ?? "/var/lib/kea/kea-leases4.csv";
        IReadOnlyList<DhcpLease> leases = [];
        if (document?["Dhcp4"]?["lease-database"]?["type"]?.ToString() is null or "memfile")
        {
            if (!Path.IsPathRooted(leasePath)) notices.Add("Relative Kea lease path cannot be read safely by LMS.");
            else
            {
                var read = await Command("cat", ["--", leasePath], true, cancellationToken);
                if (read.ExitCode == 0) leases = ParseLeases(read.StandardOutput);
                else if (!state.IsActive && read.StandardError.Contains("No such file", StringComparison.OrdinalIgnoreCase))
                    notices.Add("No leases yet. Kea creates its lease file after it starts serving clients.");
                else notices.Add("LMS could not read the lease file. Check Kea's service status and lease-file permissions.");
            }
        }
        else notices.Add("Kea uses an external lease database. Configuration is preserved; database lease inspection is unavailable in this version.");
        return new(editor, NetworkInterface.GetAllNetworkInterfaces().Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(nic => nic.Name).ToArray(), leases, notices, readOnly, raw)
        { DocumentationConfiguration = documentationConfiguration, InterfaceNetworks = ReadInterfaceNetworks() };
    }

    public static bool IsDocumentationSubnet(string subnet)
    {
        if (!IPAddress.TryParse(subnet.Split('/')[0], out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2 ||
               bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100 ||
               bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113;
    }

    public static string NetworkPrefix(IPAddress address, int prefix)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || prefix is < 1 or > 32)
            throw new ArgumentException("An IPv4 interface address and prefix are required.");
        var bytes = address.GetAddressBytes();
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] &= (byte)(255 << (8 - Math.Clamp(prefix - index * 8, 0, 8)));
        return new IPAddress(bytes) + "/" + prefix;
    }

    public static bool IsPrivateSubnet(string subnet)
    {
        var parts = subnet.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], out var prefix) || prefix is < 8 or > 30) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || prefix >= 12 && bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               prefix >= 16 && bytes[0] == 192 && bytes[1] == 168;
    }

    public static IReadOnlyList<DhcpInterfaceNetwork> ReadInterfaceNetworks() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(nic => nic.GetIPProperties().UnicastAddresses
            .Where(address => address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && address.PrefixLength is >= 8 and <= 30 &&
                IsPrivateSubnet(NetworkPrefix(address.Address, address.PrefixLength)))
            .Select(address => new DhcpInterfaceNetwork(nic.Name, address.Address.ToString(), NetworkPrefix(address.Address, address.PrefixLength))))
        .ToArray();

    public static JsonObject ParseConfiguration(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject { ["Dhcp4"] = new JsonObject() };
        if (raw.Contains("<?include", StringComparison.Ordinal)) throw new InvalidOperationException("Included configuration files require manual review; LMS will not flatten them.");
        // Kea accepts shell comments in addition to JSON comments. Preserve '#' inside strings.
        var clean = new StringBuilder(); bool quoted = false, escaped = false, comment = false;
        foreach (var character in raw)
        {
            if (comment) { if (character == '\n') { comment = false; clean.Append(character); } continue; }
            if (!quoted && character == '#') { comment = true; continue; }
            clean.Append(character);
            if (character == '"' && !escaped) quoted = !quoted;
            escaped = quoted && character == '\\' && !escaped;
        }
        return JsonNode.Parse(clean.ToString(), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject()
            ?? throw new InvalidOperationException("Missing Kea document.");
    }

    public static JsonObject BuildCandidate(string original, DhcpEditor editor)
    {
        Validate(editor);
        var document = ParseConfiguration(original);
        var dhcp = document["Dhcp4"] as JsonObject ?? throw new InvalidOperationException("Missing Dhcp4 configuration.");
        var interfaces = dhcp["interfaces-config"] as JsonObject;
        if (interfaces is null) dhcp["interfaces-config"] = interfaces = new();
        // Preserve other configured interfaces rather than silently disabling their subnets.
        var interfaceNames = interfaces["interfaces"]?.AsArray().Select(value => value?.ToString() ?? "").ToList() ?? [];
        if (interfaceNames.Contains("*")) throw new InvalidOperationException("Wildcard interfaces must be reviewed manually first.");
        if (!interfaceNames.Contains(editor.Interface)) interfaceNames.Add(editor.Interface);
        interfaces["interfaces"] = new JsonArray(interfaceNames.Select(name => JsonValue.Create(name) as JsonNode).ToArray());
        var subnets = dhcp["subnet4"] as JsonArray;
        if (subnets is null) dhcp["subnet4"] = subnets = new();
        var subnet = subnets.OfType<JsonObject>().FirstOrDefault(value => value["id"]?.GetValue<uint>() == editor.SubnetId);
        if (subnet is not null && IsDocumentationSubnet(subnet["subnet"]?.ToString() ?? ""))
        {
            // The adoption prompt explicitly explains that this replaces the example subnet.
            // Do not migrate sample boot settings or client-id reservations into a real LAN.
            var index = subnets.IndexOf(subnet);
            subnet = new JsonObject { ["id"] = editor.SubnetId };
            subnets[index] = subnet;
        }
        if (subnet is null) { subnet = new() { ["id"] = editor.SubnetId }; subnets.Add(subnet); }
        if (subnet["pools"] is JsonArray { Count: > 1 } || dhcp["shared-networks"] is JsonArray { Count: > 0 })
            throw new InvalidOperationException("The simple editor cannot replace multiple pools or shared networks.");
        subnet["subnet"] = editor.Subnet; subnet["interface"] = editor.Interface;
        subnet["valid-lifetime"] = editor.LeaseSeconds;
        var pool = (subnet["pools"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?.DeepClone().AsObject() ?? new JsonObject();
        pool["pool"] = editor.PoolStart + " - " + editor.PoolEnd;
        subnet["pools"] = new JsonArray(pool);
        var options = subnet["option-data"] as JsonArray;
        if (options is null) subnet["option-data"] = options = new();
        SetOption(options, "routers", editor.Gateway); SetOption(options, "domain-name-servers", editor.DnsServers);
        var prior = subnet["reservations"] as JsonArray ?? new();
        var updated = new JsonArray();
        // Reservations identified by client-id/duid/flex-id remain untouched.
        foreach (var item in prior.OfType<JsonObject>().Where(item => item["hw-address"] is null)) updated.Add(item.DeepClone());
        foreach (var reservation in editor.Reservations)
        {
            var item = prior.OfType<JsonObject>().FirstOrDefault(item => string.Equals(item["hw-address"]?.ToString(), reservation.Mac, StringComparison.OrdinalIgnoreCase))?.DeepClone().AsObject() ?? new();
            item["hw-address"] = reservation.Mac; item["ip-address"] = reservation.Address;
            if (reservation.Hostname.Length > 0) item["hostname"] = reservation.Hostname; else item.Remove("hostname");
            updated.Add(item);
        }
        subnet["reservations"] = updated;
        if (dhcp["lease-database"] is null) dhcp["lease-database"] = new JsonObject { ["type"] = "memfile", ["persist"] = true };
        return document;
    }
    private static void SetOption(JsonArray options, string name, string data)
    {
        var existing = options.OfType<JsonObject>().FirstOrDefault(option => option["name"]?.ToString() == name);
        if (existing is null) { existing = new() { ["name"] = name }; options.Add(existing); }
        existing["data"] = data;
    }
    private static string Option(JsonObject subnet, JsonObject dhcp, string name) =>
        (subnet["option-data"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(item => item["name"]?.ToString() == name)?["data"]?.ToString() ??
        (dhcp["option-data"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(item => item["name"]?.ToString() == name)?["data"]?.ToString() ?? "";

    public static void Validate(DhcpEditor editor)
    {
        if (!Regex.IsMatch(editor.Interface, "^[a-zA-Z0-9_.:-]{1,64}$")) throw new InvalidOperationException("Select a valid listening interface.");
        var parts = editor.Subnet.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var prefix) || prefix is < 8 or > 30) throw new InvalidOperationException("Use an IPv4 network and prefix from /8 to /30.");
        var network = IPv4(parts[0]); var mask = uint.MaxValue << (32 - prefix); var broadcast = network | ~mask;
        if (IsDocumentationSubnet(editor.Subnet)) throw new InvalidOperationException("This is a documentation-only network. Select the real network served by this interface.");
        if (!IsPrivateSubnet(editor.Subnet)) throw new InvalidOperationException("Use the interface's real RFC1918 private subnet (10/8, 172.16/12 or 192.168/16). LMS does not configure public or documentation networks as a LAN.");
        if ((network & mask) != network) throw new InvalidOperationException("Subnet must be the network address, for example 192.168.1.0/24.");
        var start = IPv4(editor.PoolStart); var end = IPv4(editor.PoolEnd); var router = IPv4(editor.Gateway);
        if (start <= network || end >= broadcast || start > end || router <= network || router >= broadcast)
            throw new InvalidOperationException("Pool and gateway must be usable addresses in the selected subnet.");
        if (router >= start && router <= end) throw new InvalidOperationException("The gateway must be outside the dynamic pool.");
        var dns = editor.DnsServers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (dns.Length == 0) throw new InvalidOperationException("Enter at least one DNS server.");
        foreach (var address in dns) IPv4(address);
        if (editor.LeaseSeconds is < 60 or > 31536000) throw new InvalidOperationException("Lease duration must be between 60 seconds and one year.");
        if (editor.SubnetId == 0) throw new InvalidOperationException("Subnet ID must be positive.");
        if (editor.Reservations.Select(item => item.Mac.ToUpperInvariant()).Distinct().Count() != editor.Reservations.Count ||
            editor.Reservations.Select(item => item.Address).Distinct().Count() != editor.Reservations.Count) throw new InvalidOperationException("Reservations must have unique MAC and IP addresses.");
        foreach (var reservation in editor.Reservations)
        {
            if (!Regex.IsMatch(reservation.Mac, "^(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$")) throw new InvalidOperationException("A reservation requires a valid MAC address.");
            var address = IPv4(reservation.Address);
            if (address <= network || address >= broadcast || address == router) throw new InvalidOperationException("Reserved address must be usable and must not be the gateway.");
            if (reservation.Hostname.Length > 0 && !Regex.IsMatch(reservation.Hostname, "^[a-zA-Z0-9][a-zA-Z0-9.-]{0,252}$")) throw new InvalidOperationException("Invalid reservation hostname.");
        }
    }
    private static uint IPv4(string text)
    {
        if (!IPAddress.TryParse(text, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidOperationException("Not a valid IPv4 address: " + text);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
    }

    public async Task<IReadOnlyList<string>> CheckReservationAsync(DhcpReservation reservation, CancellationToken cancellationToken = default)
    {
        IPv4(reservation.Address);
        var workspace = await GetAsync(cancellationToken);
        var conflicts = new List<string>();
        foreach (var existing in workspace.Editor.Reservations.Where(item => item.Address == reservation.Address && !item.Mac.Equals(reservation.Mac, StringComparison.OrdinalIgnoreCase)))
            conflicts.Add($"Reserved to {existing.Mac} ({existing.Hostname}).");
        var document = ParseConfiguration(workspace.ExistingConfiguration);
        var reservationLists = new List<JsonArray>();
        if (document["Dhcp4"]?["reservations"] is JsonArray global) reservationLists.Add(global);
        if (document["Dhcp4"]?["subnet4"] is JsonArray subnets)
            reservationLists.AddRange(subnets.OfType<JsonObject>().Select(item => item["reservations"]).OfType<JsonArray>());
        foreach (var existing in reservationLists.SelectMany(list => list.OfType<JsonObject>()).Where(item =>
            item["ip-address"]?.ToString() == reservation.Address && !string.Equals(item["hw-address"]?.ToString(), reservation.Mac, StringComparison.OrdinalIgnoreCase)))
            conflicts.Add("Another configured reservation already uses this address (including client-id or other subnet reservations).");
        foreach (var lease in workspace.Leases.Where(item => item.Active && item.Address == reservation.Address && !item.Mac.Equals(reservation.Mac, StringComparison.OrdinalIgnoreCase)))
            conflicts.Add($"Active lease belongs to {lease.Mac} ({lease.Hostname}) until {lease.ExpiresUtc:g}.");
        var neighbours = await Command("ip", ["-j", "neigh", "show", "to", reservation.Address], false, cancellationToken);
        if (neighbours.ExitCode != 0) throw new InvalidOperationException("Cannot check neighbour conflicts: " + neighbours.StandardError);
        foreach (var neighbour in InfrastructureDiagnosticsService.ParseNeighbours(neighbours.StandardOutput)
            .Where(item => item.Mac.Length > 0 && !item.Mac.Equals(reservation.Mac, StringComparison.OrdinalIgnoreCase)))
            conflicts.Add("Neighbour table associates this address with " + neighbour.Mac + ".");
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            if (nic.GetIPProperties().UnicastAddresses.Any(address => address.Address.ToString() == reservation.Address))
                conflicts.Add("This address belongs to an LMS host interface.");
        return conflicts;
    }

    public async Task SaveAsync(DhcpEditor editor, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        var candidatePath = Config + ".lms-candidate-" + Guid.NewGuid().ToString("N");
        string? backup = null; string candidate = ""; ServiceState? oldState = null; bool activated = false;
        try
        {
            if (!editor.AdoptExisting) throw new InvalidOperationException("Explicitly allow LMS to manage this configuration before saving.");
            if (editor.Enabled && !editor.ConfirmSoleDhcpServer) throw new InvalidOperationException("Confirm this host should provide DHCP on this network.");
            if (!NetworkInterface.GetAllNetworkInterfaces().Any(nic => nic.Name == editor.Interface)) throw new InvalidOperationException("The selected interface no longer exists.");
            var workspace = await GetAsync(cancellationToken);
            if (workspace.ReadOnly) throw new InvalidOperationException("This existing configuration requires manual review; it has not been changed.");
            if (Hash(workspace.ExistingConfiguration) != editor.OriginalHash) throw new InvalidOperationException("Kea configuration changed since it was opened. Refresh before saving.");
            foreach (var reservation in editor.Reservations)
            {
                var conflicts = await CheckReservationAsync(reservation, cancellationToken);
                if (conflicts.Count > 0) throw new InvalidOperationException($"Cannot reserve {reservation.Address}: {string.Join(" ", conflicts)}");
            }
            candidate = BuildCandidate(workspace.ExistingConfiguration, editor).ToJsonString(new() { WriteIndented = true });
            var prepare = await Command("python3", ["-c", "import os,sys; p=sys.argv[1]; fd=os.open(p,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o640); f=os.fdopen(fd,'wb'); f.write(sys.stdin.buffer.read()); f.flush(); os.fsync(f.fileno()); f.close()", candidatePath], true, cancellationToken, Encoding.UTF8.GetBytes(candidate));
            Require(prepare, "Could not write candidate");
            Require(await Command("kea-dhcp4", ["-t", candidatePath], true, cancellationToken), "Kea rejected the candidate; the active configuration is unchanged");
            oldState = (await services.InspectAsync([Unit], cancellationToken)).Single();
            backup = Config + ".lms-backup-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N");
            Require(await Command("python3", ["-c", ActivateScript, Config, candidatePath, backup, editor.OriginalHash], true, cancellationToken), "Could not activate candidate");
            activated = true;
            await Action(editor.Enabled ? oldState.IsActive ? ServiceActionKind.Restart : ServiceActionKind.Start : ServiceActionKind.Stop, cancellationToken);
            var state = (await services.InspectAsync([Unit], cancellationToken)).Single();
            if (state.IsActive != editor.Enabled) throw new InvalidOperationException("Kea did not reach the requested service state.");
            if (editor.Enabled)
                for (var sample = 0; sample < 3; sample++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                    if (!(await services.InspectAsync([Unit], cancellationToken)).Single().IsActive)
                        throw new InvalidOperationException("Kea stopped during startup verification.");
                }
            await Action(editor.Enabled ? ServiceActionKind.Enable : ServiceActionKind.Disable, cancellationToken);
        }
        catch (Exception error) when (activated && backup is not null && oldState is not null)
        {
            // Recovery deliberately outlives a cancelled browser request.
            var restore = await Command("python3", ["-c", RestoreScript, Config, backup, Hash(candidate)], true, CancellationToken.None);
            if (restore.ExitCode != 0) throw new InvalidOperationException($"Kea change failed: {error.Message}. Rollback failed; backup: {backup}. {restore.StandardError}", error);
            try
            {
                await Action(oldState.IsActive ? ServiceActionKind.Restart : ServiceActionKind.Stop, CancellationToken.None);
                await Action(oldState.IsEnabled ? ServiceActionKind.Enable : ServiceActionKind.Disable, CancellationToken.None);
                var state = (await services.InspectAsync([Unit], CancellationToken.None)).Single();
                if (state.IsActive != oldState.IsActive) throw new InvalidOperationException("Previous service state was not restored.");
            }
            catch (Exception recovery) { throw new InvalidOperationException($"Kea change failed: {error.Message}. Configuration restored from {backup}, but service recovery failed: {recovery.Message}", error); }
            throw new InvalidOperationException($"Kea change failed: {error.Message}. Previous configuration and service state restored. Backup: {backup}", error);
        }
        finally
        {
            try { await Command("rm", ["-f", "--", candidatePath], true, CancellationToken.None); }
            finally { Gate.Release(); }
        }
    }
    private async Task Action(ServiceActionKind action, CancellationToken token)
    {
        var logs = await services.ApplyActionsAsync([new(action, Unit, "Manage Kea DHCP", false, "")], false, token);
        if (logs.Any(log => log.Level == OperationLogLevel.Error)) throw new InvalidOperationException(string.Join("; ", logs.Select(log => log.Message + " " + log.StandardError)));
    }
    private static void Require(LinuxCommandResult result, string message)
    { if (result.ExitCode != 0) throw new InvalidOperationException(message + ": " + result.StandardError + result.StandardOutput); }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private const string ActivateScript = """
        import os,sys,pathlib,hashlib,shutil
        live,candidate,backup,expected=sys.argv[1:]
        p=pathlib.Path(live)
        if p.is_symlink(): raise RuntimeError('Refusing to replace a symbolic link')
        data=p.read_bytes() if p.exists() else b''
        if hashlib.sha256(data).hexdigest()!=expected: raise RuntimeError('Configuration changed concurrently')
        if p.exists():
            shutil.copy2(live,backup)
            os.chmod(backup,0o600)
            with open(backup,'rb') as f: os.fsync(f.fileno())
            st=p.stat(); os.chown(candidate,st.st_uid,st.st_gid); os.chmod(candidate,st.st_mode & 0o777)
        else:
            pathlib.Path(backup).write_bytes(b''); os.chmod(backup,0o600)
            marker=pathlib.Path(backup+'.missing'); marker.write_bytes(b''); os.chmod(marker,0o600)
            os.chmod(candidate,0o644)
        fd=os.open(str(p.parent),os.O_DIRECTORY); os.fsync(fd); os.close(fd)
        os.replace(candidate,live)
        fd=os.open(str(p.parent),os.O_DIRECTORY); os.fsync(fd); os.close(fd)
        """;
    private const string RestoreScript = """
        import os,sys,pathlib,hashlib,uuid
        live,backup,expected=sys.argv[1:]
        p=pathlib.Path(live)
        if p.is_symlink() or hashlib.sha256(p.read_bytes()).hexdigest()!=expected: raise RuntimeError('Live configuration changed after activation; refusing to overwrite it')
        if pathlib.Path(backup+'.missing').exists():
            p.unlink(); fd=os.open(str(p.parent),os.O_DIRECTORY); os.fsync(fd); os.close(fd); sys.exit(0)
        temporary=live+'.lms-restore-'+uuid.uuid4().hex
        st=p.stat()
        with open(temporary,'xb') as output: output.write(pathlib.Path(backup).read_bytes()); output.flush(); os.fsync(output.fileno())
        os.chown(temporary,st.st_uid,st.st_gid); os.chmod(temporary,st.st_mode & 0o777)
        os.replace(temporary,live)
        fd=os.open(str(p.parent),os.O_DIRECTORY); os.fsync(fd); os.close(fd)
        """;

    public static IReadOnlyList<DhcpLease> ParseLeases(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return [];
        var header = Csv(lines[0]); var leases = new Dictionary<string, DhcpLease>();
        foreach (var line in lines.Skip(1))
        {
            var values = Csv(line); string Field(string name) { var index = Array.IndexOf(header, name); return index >= 0 && index < values.Length ? values[index] : ""; }
            var address = Field("address");
            if (!IPAddress.TryParse(address, out _) || !long.TryParse(Field("expire"), out var expiry) || expiry is < 0 or > 253402300799) continue;
            var expires = DateTimeOffset.FromUnixTimeSeconds(expiry);
            leases[address] = new(address, Field("hwaddr"), Field("hostname"), expires,
                expires > DateTimeOffset.UtcNow && Field("valid_lifetime") != "0" && Field("state") is "0" or "");
        }
        return leases.Values.OrderBy(lease => lease.Address).ToArray();
    }
    private static string[] Csv(string line)
    {
        var values = new List<string>(); var value = new StringBuilder(); bool quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; } else quoted = !quoted; }
            else if (line[i] == ',' && !quoted) { values.Add(value.ToString()); value.Clear(); } else value.Append(line[i]);
        }
        values.Add(value.ToString().TrimEnd('\r')); return values.ToArray();
    }
}
