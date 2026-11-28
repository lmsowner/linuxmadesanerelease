// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.HomeLab;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Services.ConfigurationSummary;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed record ContainerVolume(string Name, string Path, bool ReadOnly)
{
    public string Binding => $"{Name}:{Path}:{(ReadOnly ? "ro" : "rw")}";
}
public sealed record ContainerInstall(string Name, string Image, Dictionary<string,string> Environment,
    string Volume, string MountPath, IReadOnlyList<DockerPortBinding> Ports)
{
    public IReadOnlyList<ContainerVolume> AdditionalVolumes { get; init; } = [];
    public bool VolumeReadOnly { get; init; }
    public string RestartPolicy { get; init; } = "unless-stopped";
    public IReadOnlyList<string> Command { get; init; } = [];
}
public sealed record ContainerRow(DockerContainerObservation Container, string Owner, bool CanControl, bool CanEdit,
    IReadOnlySet<(int Port, string Protocol)>? NetworkConfigurable = null, bool NeedsHomeLabRecovery = false)
{
    public bool CanOperateComposeContainer { get; init; }
}
public sealed record ContainerSettingsDraft(string Image, IReadOnlyList<string> EnvironmentKeys, string Volume, string MountPath);
public sealed record ContainerSettingsEdit(string Image, Dictionary<string,string?> Environment, string Volume, string MountPath);
public sealed record ContainerStartup(string Policy, string State, int RestartCount, int ExitCode, bool OutOfMemory, bool HomeLab, bool NeedsRecovery)
{
    public IReadOnlyList<ContainerConfigMount> ConfigMounts { get; init; } = [];
}
public sealed record ContainerConfigMount(string Destination, string HostPath, bool Writable);
public enum ContainerAction { Start, Stop, Restart, Logs, Delete }

public sealed partial class LocalContainerService(ILinuxCommandRunner runner, IDockerInventoryReader inventory,
    IHomeLabService homeLab, LinuxMadeSaneDbContext db, ISavedCredentialAccessContext access,
    ISecretStore secrets, ILogger<LocalContainerService> audit)
{
    private const string ConfigLabel = "com.linuxmadesane.container.config";
    private const string ImagePathComponent = @"[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*";
    private const string RegistryComponent = @"[a-z0-9](?:[a-z0-9-]*[a-z0-9])?";
    private const string ImagePattern = @"\A(?:(?:" + RegistryComponent + @"(?:\." + RegistryComponent +
        @")*|\[[a-fA-F0-9:]+\])(?::[0-9]{1,5})?/)?(?:" + ImagePathComponent + @"/)*" + ImagePathComponent +
        @"(?::[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})?(?:@sha256:[0-9a-f]{64})?\z";
    private const string BackupPrefix = "lms-port-backup-";
    private static readonly SemaphoreSlim[] Locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    private static SemaphoreSlim Gate(string name) => Locks[(uint)StringComparer.Ordinal.GetHashCode(name) % Locks.Length];
    private static bool IsRecoveryName(string name) => name.StartsWith(BackupPrefix, StringComparison.Ordinal) || name.StartsWith("lms-pending-", StringComparison.Ordinal);
    private static bool ValidName(string name) => Regex.IsMatch(name, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,126}\z");
    private static bool ValidId(string id) => Regex.IsMatch(id, @"\A[0-9a-f]{64}\z");
    private static string Purpose(string name) => "LMS container settings: " + name;

    private async Task AdminAsync(string operation, string target)
    {
        var user = await access.GetAuthenticatedUserIdAsync() ?? throw new UnauthorizedAccessException("LMS host administrator access is required.");
        audit.LogInformation("Local Docker {Operation} for {Target} requested by {User}", operation, target, user);
    }

    private async Task<LinuxCommandResult> RunAsync(IReadOnlyList<string> args, IReadOnlySet<int>? sensitive = null)
    {
        var request = new LinuxCommandRequest("docker", args, false, TimeSpan.FromMinutes(4), "LMS local container " + args[0])
            { SensitiveArgumentIndexes = sensitive, IsOptionalExternalTool = true, OutputTailCharacters = args[0] == "logs" ? 32768 : null };
        var result = await runner.RunAsync(request, false);
        // Only retry a daemon socket permission error. A failed mutation may already have taken effect.
        if (result.ExitCode != 0 && result.StandardError.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            && result.StandardError.Contains("docker.sock", StringComparison.OrdinalIgnoreCase))
            result = await runner.RunAsync(request with { RequiresSudo = true }, false);
        return result;
    }

    private async Task<string> RequireAsync(IReadOnlyList<string> args, IReadOnlySet<int>? sensitive = null)
    {
        var result = await RunAsync(args, sensitive);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(args[0] switch {
                "pull" => "Image pull failed. Check the public image name, tag and registry connectivity.",
                "start" => "Docker could not start the container. Check that the listen address exists and the host port is free.",
                "inspect" => "The container changed or disappeared. Refresh the list.",
                _ => $"Docker could not complete {args[0]}. Refresh the list and check Docker in Services."
            });
        return result.StandardOutput.Trim();
    }

    private async Task<JsonNode> InspectAsync(string id)
    {
        if (!ValidId(id)) throw new InvalidOperationException("Select a container from the current inventory.");
        var json = await RequireAsync(["inspect", "--type", "container", id]);
        var node = JsonNode.Parse(json)?[0];
        if (node?["Id"]?.ToString() != id) throw new InvalidOperationException("Container identity changed.");
        return node;
    }

    public async Task<(DockerAvailability Status, List<ContainerRow> Rows)> ListAsync()
    {
        await AdminAsync("inventory", "local host");
        var snapshot = await inventory.ReadInventoryAsync();
        var installs = await db.HomeLabInstallations.AsNoTracking().ToDictionaryAsync(i => i.ContainerName);
        var rows = new List<ContainerRow>();
        foreach (var item in snapshot.Containers)
        {
            var install = installs.GetValueOrDefault(item.Name);
            var external = install != null && HomeLabService.IsExternallyManaged(install);
            var owner = external ? "External Docker manager" : item.ComposeProject != null ? $"Compose · {item.ComposeProject}" :
                install != null ? "Home Lab" : item.HomeLabLabel ? "Home Lab · installation missing" :
                item.SimpleConfig != null ? "LMS image install" : "Standalone";
            var recover = IsRecoveryName(item.Name);
            bool control = !external && item.ComposeProject == null && !recover;
            bool editable = install != null ? HomeLabService.SupportsPortEditing(install) && item.Bindings.Count > 0 :
                item.SimpleConfig != null && item.NetworkMode is "bridge" or "default";
            var networkConfigurable = install == null ? null : HomeLabCatalog.GetApp(install.AppId).Ports
                .Where(p => p.HostBindingAddress is "0.0.0.0" or "::").Select(p => (p.ContainerPort,p.Protocol)).ToHashSet();
            rows.Add(new(item, recover ? "Interrupted port edit · recover in Docker" : owner, control, control && editable && !SharesNamespace(snapshot,item.Id,item.Name), networkConfigurable,
                install == null && item.HomeLabLabel && RequiresSavedNetwork(item.HomeLabAppId))
            {
                CanOperateComposeContainer = item.ComposeProject != null && !recover && install == null && !item.HomeLabLabel
            });
        }
        return (snapshot.Availability, rows);
    }

    private static bool SharesNamespace(DockerInventory snapshot, string id, string name) =>
        snapshot.Containers.Any(c => HomeLabDockerNetworkMode.UsesContainerNamespace(c.NetworkMode, name, id));

    internal static bool ValidImage(string image) =>
        image.Length <= 255 && Regex.IsMatch(image, ImagePattern, RegexOptions.NonBacktracking);

    private async Task<Guid?> CheckOwnerAsync(JsonNode node, bool allowUnregistered = false, bool allowComposeLifecycle = false)
    {
        var name = node["Name"]!.ToString().TrimStart('/');
        var labels = node["Config"]?["Labels"];
        if (IsRecoveryName(name))
            throw new InvalidOperationException("Use the container owner's management workflow.");
        var installation = await db.HomeLabInstallations.AsNoTracking().SingleOrDefaultAsync(i => i.ContainerName == name);
        if (labels?["com.docker.compose.project"] != null)
        {
            if (allowComposeLifecycle && installation == null && labels?["com.linuxmadesane.homelab"] == null) return null;
            throw new InvalidOperationException("This container’s configuration belongs to its Compose project.");
        }
        if (installation == null)
        {
            if (labels?["com.linuxmadesane.homelab"] == null) return null;
            var app = labels?["com.linuxmadesane.homelab.app"]?.ToString();
            if (allowUnregistered && !string.IsNullOrEmpty(app) &&
                Guid.TryParse(labels?["com.linuxmadesane.homelab.deployment"]?.ToString(), out var deployment) &&
                !await db.HomeLabInstallations.AnyAsync(i => i.DeploymentId == deployment && i.AppId == app))
                return null;
        }
        if (installation == null || HomeLabService.IsExternallyManaged(installation) ||
            labels?["com.linuxmadesane.homelab.deployment"]?.ToString() != installation.DeploymentId.ToString())
            throw new InvalidOperationException("Home Lab ownership changed. Refresh the list.");
        return installation.Id;
    }

    private static bool RequiresSavedNetwork(string? appId) =>
        HomeLabCatalog.Apps.FirstOrDefault(a => a.Id == appId)?.RequiresVpnGateway != false;

    private static bool NeedsHomeLabRecovery(JsonNode node, Guid? installation) =>
        installation == null && node["Config"]?["Labels"]?["com.linuxmadesane.homelab"] != null &&
        RequiresSavedNetwork(node["Config"]?["Labels"]?["com.linuxmadesane.homelab.app"]?.ToString());

    private static string StartupPolicy(JsonNode node)
    {
        var policy = node["HostConfig"]!["RestartPolicy"]!;
        var name = policy["Name"]!.ToString();
        var limit = policy["MaximumRetryCount"]!.GetValue<int>();
        return name == "on-failure" && limit > 0 ? $"on-failure:{limit}" : name;
    }

    public async Task<ContainerStartup> GetStartupAsync(string id)
    {
        await AdminAsync("read startup", id);
        var node = await InspectAsync(id);
        var installation = await CheckOwnerAsync(node, allowUnregistered: true);
        var state = node["State"]!;
        return new(StartupPolicy(node), state["Status"]!.ToString(), node["RestartCount"]!.GetValue<int>(),
            state["ExitCode"]!.GetValue<int>(), state["OOMKilled"]!.GetValue<bool>(), installation.HasValue, NeedsHomeLabRecovery(node, installation))
        { ConfigMounts = ConfigMounts(node) };
    }

    internal static IReadOnlyList<ContainerConfigMount> ConfigMounts(JsonNode node)
    {
        var appId = node["Config"]?["Labels"]?["com.linuxmadesane.homelab.app"]?.ToString();
        var app = HomeLabCatalog.Apps.FirstOrDefault(a => a.Id == appId);
        var mounts = new List<ContainerConfigMount>();
        foreach (var mount in node["Mounts"]?.AsArray() ?? [])
        {
            var source = mount?["Source"]?.ToString();
            var destination = mount?["Destination"]?.ToString();
            if (mount?["Type"]?.ToString() is not ("bind" or "volume") ||
                string.IsNullOrEmpty(source) || !Path.IsPathFullyQualified(source) || string.IsNullOrEmpty(destination))
                continue;
            if (app != null && !app.Volumes.Any(v => v.Kind == HomeLabStorageKind.Configuration && v.ContainerPath == destination))
                continue;
            mounts.Add(new(destination, source, mount["RW"]?.GetValue<bool>() == true));
        }
        return mounts;
    }

    public async Task SetStartupAsync(string id, string expectedPolicy, string policy, bool stop)
    {
        await AdminAsync(stop ? "disable" : "change startup", id);
        var gate = Gate((await InspectAsync(id))["Name"]!.ToString().TrimStart('/'));
        await gate.WaitAsync();
        try
        {
            var node = await InspectAsync(id);
            var installation = await CheckOwnerAsync(node, allowUnregistered: true);
            if (NeedsHomeLabRecovery(node, installation) && policy != "no" && policy != expectedPolicy)
                throw new InvalidOperationException("Restore this app through Home Lab before enabling automatic restarts.");
            if (StartupPolicy(node) != expectedPolicy)
                throw new InvalidOperationException("The restart policy changed. Reopen settings to see the current value.");
            if (stop && policy != "no" || policy != expectedPolicy && policy is not ("always" or "unless-stopped" or "on-failure" or "no"))
                throw new InvalidOperationException("Select a supported startup policy.");
            if (policy != expectedPolicy || installation.HasValue && stop)
            {
                if (installation is Guid app)
                {
                    if (policy is not ("always" or "no")) throw new InvalidOperationException("Select automatic or manual startup for this Home Lab app.");
                    var result = await homeLab.SetStartOnBootAsync([app], policy == "always");
                    if (!result.Succeeded) throw new InvalidOperationException("Home Lab could not save the startup policy.");
                }
                else await RequireAsync(["update", "--restart", policy, id]);
            }
            if (stop && (node["State"]!["Running"]!.GetValue<bool>() || node["State"]!["Status"]?.ToString() == "restarting"))
            {
                if (installation is Guid app)
                {
                    var result = await homeLab.ExecuteAsync(app, HomeLabLifecycleAction.Stop, id);
                    if (!result.Succeeded) throw new InvalidOperationException("Automatic restarts are off. Home Lab could not stop the container. Retry Stop.");
                }
                else if ((await RunAsync(["stop", id])).ExitCode != 0)
                    throw new InvalidOperationException("Automatic restarts are off. Docker could not stop the container. Retry Stop.");
            }
        }
        finally { gate.Release(); }
    }

    public async Task<string> ActAsync(string id, ContainerAction action)
    {
        await AdminAsync(action.ToString(), id);
        var gate = Gate((await InspectAsync(id))["Name"]!.ToString().TrimStart('/'));
        await gate.WaitAsync();
        try
        {
            var node = await InspectAsync(id);
            var installation = await CheckOwnerAsync(node, allowUnregistered: true,
                allowComposeLifecycle: action is ContainerAction.Start or ContainerAction.Stop or ContainerAction.Restart or ContainerAction.Logs);
            if (action is ContainerAction.Start or ContainerAction.Restart && NeedsHomeLabRecovery(node, installation))
                throw new InvalidOperationException("Restore this app through Home Lab so LMS can verify its network settings before starting it.");
            if (action == ContainerAction.Logs)
            {
                string text;
                if (installation is Guid app)
                {
                    var logs = await homeLab.GetLogsAsync(app, id);
                    if (!logs.Succeeded) throw new InvalidOperationException("Could not read Home Lab logs.");
                    text = logs.Logs;
                }
                else
                {
                    var logs = await RunAsync(["logs", "--tail", "100", id]);
                    if (logs.ExitCode != 0) throw new InvalidOperationException("Docker could not read recent logs.");
                    text = logs.StandardOutput + logs.StandardError;
                }
                foreach (var variable in node["Config"]?["Env"]?.AsArray() ?? [])
                {
                    var value = variable?.ToString().Split('=', 2).ElementAtOrDefault(1);
                    if (!string.IsNullOrEmpty(value)) text = text.Replace(value, "[redacted]", StringComparison.Ordinal);
                }
                text = HomeLabService.RedactConfiguration(text);
                return text.Length == 0 ? "No recent logs." : text[^Math.Min(32768, text.Length)..];
            }
            if (action == ContainerAction.Delete)
            {
                var name = node["Name"]!.ToString().TrimStart('/');
                var snapshot = await inventory.ReadInventoryAsync();
                if (snapshot.Availability != DockerAvailability.Ready || SharesNamespace(snapshot, id, name))
                    throw new InvalidOperationException("Check containers using this network namespace before deleting its owner.");
                if (installation is Guid app)
                {
                    var removed = await homeLab.ExecuteAsync(app, HomeLabLifecycleAction.Remove, id);
                    if (!removed.Succeeded) throw new InvalidOperationException(removed.Detail);
                }
                else
                {
                    var reference = node["Config"]?["Labels"]?[ConfigLabel]?.ToString();
                    var ownsSettings = reference != null && (await secrets.GetMetadataAsync(reference))?.Purpose == Purpose(name);
                    await RequireAsync(["rm", "--force", id]);
                    if (ownsSettings) await secrets.DeleteSecretAsync(reference!);
                }
                return $"Deleted {name}. Mounted data was preserved.";
            }
            var lifecycle = action switch { ContainerAction.Start => HomeLabLifecycleAction.Start,
                ContainerAction.Stop => HomeLabLifecycleAction.Stop, ContainerAction.Restart => HomeLabLifecycleAction.Restart,
                _ => throw new InvalidOperationException("Unsupported container action.") };
            if (installation is Guid installationId)
            {
                var result = await homeLab.ExecuteAsync(installationId, lifecycle, id);
                if (!result.Succeeded) throw new InvalidOperationException("Home Lab could not complete the action. Check its installation status.");
            }
            else await RequireAsync([action.ToString().ToLowerInvariant(), id]);
            return $"{action} completed.";
        }
        finally { gate.Release(); }
    }

    internal static void ValidatePorts(IReadOnlyList<DockerPortBinding> ports)
    {
        if (ports.Count > 64 || ports.Any(p => p.HostPort is < 1 or > 65535 || p.ContainerPort is < 1 or > 65535 ||
            p.Protocol is not ("tcp" or "udp") || !IPAddress.TryParse(p.Address, out _)) ||
            ports.GroupBy(p => (p.Address, p.HostPort, p.Protocol)).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Use distinct listen bindings, a valid IP address, TCP or UDP, and ports from 1 to 65535.");
    }

    private static void ValidateImageAndVolume(ContainerInstall config)
    {
        if (!ValidImage(config.Image))
            throw new InvalidOperationException("Enter a public Docker image reference.");
        var volumes = Volumes(config);
        if (volumes.Select(v => v.Path).Distinct(StringComparer.Ordinal).Count() != volumes.Count)
            throw new InvalidOperationException("Use a different container path for each volume.");
        foreach (var volume in volumes) ValidateVolume(volume);
    }

    internal static void ValidateVolume(ContainerVolume volume)
    {
        if (!ValidName(volume.Name) || !Regex.IsMatch(volume.Path, @"\A/(?:[\w.-]+/)*[\w.-]+\z") ||
            volume.Path.Split('/').Any(s => s is ".." or "."))
            throw new InvalidOperationException("Use a named Docker volume with an absolute container path.");
    }

    private static List<ContainerVolume> Volumes(ContainerInstall config)
    {
        var volumes = new List<ContainerVolume>();
        if (config.Volume.Length != 0 || config.MountPath.Length != 0)
            volumes.Add(new(config.Volume, config.MountPath, config.VolumeReadOnly));
        volumes.AddRange(config.AdditionalVolumes);
        return volumes;
    }

    public async Task InstallAsync(ContainerInstall config)
    {
        await AdminAsync("install", "local host");
        if (!ValidName(config.Name) || IsRecoveryName(config.Name))
            throw new InvalidOperationException("Enter a valid container name.");
        ValidateImageAndVolume(config);
        config = config with { Environment = new(HomeLabService.NormalizeContainerEnvironment(config.Environment), StringComparer.Ordinal) };
        ValidatePorts(config.Ports);
        var gate = Gate(config.Name);
        await gate.WaitAsync();
        try
        {
            if (await db.HomeLabInstallations.AnyAsync(i => i.ContainerName == config.Name))
                throw new InvalidOperationException("Home Lab already owns this name.");
            await RequireAsync(["pull", config.Image]);
            var reference = await secrets.StoreSecretAsync(JsonSerializer.Serialize(config), Purpose(config.Name));
            string? id = null;
            try
            {
                var mounts = Volumes(config).Select(v => v.Binding).ToList();
                id = await CreateAsync(config, reference, config.Name, config.Image, config.Ports, mounts, config.RestartPolicy, "bridge");
                await RequireAsync(["start", id]);
                await VerifyAsync(id, true, config.Ports);
            }
            catch
            {
                if (id != null) await RequireAsync(["rm", "-f", id]);
                await secrets.DeleteSecretAsync(reference);
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private async Task<string> CreateAsync(ContainerInstall config, string reference, string name, string image,
        IReadOnlyList<DockerPortBinding> ports, List<string> mounts, string restart, string network)
    {
        var args = new List<string> { "create", "--name", name, "--label", $"{ConfigLabel}={reference}", "--label", $"com.linuxmadesane.container.image={config.Image}", "--restart", restart, "--network", network };
        var hidden = new HashSet<int>();
        foreach (var mount in mounts) args.AddRange(["--volume", mount]);
        foreach (var port in ports)
        {
            var address = port.Address.Contains(':') ? $"[{port.Address}]" : port.Address;
            args.AddRange(["--publish", $"{address}:{port.HostPort}:{port.ContainerPort}/{port.Protocol}"]);
        }
        foreach (var variable in config.Environment)
        {
            args.AddRange(["--env", $"{variable.Key}={variable.Value}"]);
            hidden.Add(args.Count - 1);
        }
        args.Add(image);
        foreach (var argument in config.Command)
        {
            args.Add(argument);
            hidden.Add(args.Count - 1);
        }
        var id = await RequireAsync(args, hidden);
        if (!ValidId(id)) throw new InvalidOperationException("Docker did not return a container ID.");
        return id;
    }

    private static bool SameVolumes(JsonNode original, JsonNode replacement)
    {
        static HashSet<(string?, string?, bool?)> Mounts(JsonNode n) => n["Mounts"]!.AsArray()
            .Select(m => (m?["Name"]?.ToString(), m?["Destination"]?.ToString(), m?["RW"]?.GetValue<bool>()))
            .ToHashSet();
        return Mounts(original).SetEquals(Mounts(replacement));
    }

    private async Task VerifyAsync(string id, bool running, IReadOnlyList<DockerPortBinding> expected)
    {
        var inspect = await InspectAsync(id);
        var ports = running ? inspect["NetworkSettings"]?["Ports"] : inspect["HostConfig"]?["PortBindings"];
        var actual = DockerConfigurationSummaryProvider.ParseBindings(ports?.ToJsonString() ?? "{}");
        if (inspect["State"]?["Running"]?.GetValue<bool>() != running || !expected.ToHashSet().SetEquals(actual))
            throw new InvalidOperationException("Docker did not apply the requested state and bindings.");
    }

    private static bool Conflicts(DockerPortBinding requested, DockerPortBinding used)
    {
        if (requested.HostPort != used.HostPort || requested.Protocol != used.Protocol) return false;
        if (!IPAddress.TryParse(used.Address, out var existing)) return true;
        var address = IPAddress.Parse(requested.Address);
        return address.AddressFamily == existing.AddressFamily && (address.Equals(existing) ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            existing.Equals(IPAddress.Any) || existing.Equals(IPAddress.IPv6Any));
    }

    // Docker reserves a stopped container's host ports only when it starts. Probe
    // them locally without executing the workload merely to validate an edit.
    private static void CheckStoppedBindings(IReadOnlyList<DockerPortBinding> ports)
    {
        var probes = new List<Socket>();
        try
        {
            foreach (var port in ports)
            {
                var address = IPAddress.Parse(port.Address);
                var socket = new Socket(address.AddressFamily, port.Protocol == "tcp" ? SocketType.Stream : SocketType.Dgram,
                    port.Protocol == "tcp" ? ProtocolType.Tcp : ProtocolType.Udp);
                probes.Add(socket);
                socket.Bind(new IPEndPoint(address, port.HostPort));
            }
        }
        catch (SocketException)
        {
            throw new InvalidOperationException("The host port is occupied or its listen address is unavailable.");
        }
        finally { foreach (var probe in probes) probe.Dispose(); }
    }

    private async Task<(string Reference, ContainerInstall Config)> ReadConfigAsync(JsonNode node, string name)
    {
        var reference = node["Config"]?["Labels"]?[ConfigLabel]?.ToString();
        if (reference == null || (await secrets.GetMetadataAsync(reference))?.Purpose != Purpose(name))
            throw new InvalidOperationException("The saved LMS installation could not be verified.");
        var config = JsonSerializer.Deserialize<ContainerInstall>(await secrets.ResolveSecretAsync(reference) ?? "")!;
        if (config.Name != name) throw new InvalidOperationException("Saved container identity mismatch.");
        return (reference, config);
    }

    public async Task<ContainerSettingsDraft> GetSettingsAsync(string id)
    {
        await AdminAsync("read settings", id);
        var node = await InspectAsync(id);
        if (await CheckOwnerAsync(node) != null) throw new InvalidOperationException("Open Home Lab settings for this container.");
        var (_, config) = await ReadConfigAsync(node, node["Name"]!.ToString().TrimStart('/'));
        return new(config.Image, config.Environment.Keys.Order().ToArray(), config.Volume, config.MountPath);
    }

    public async Task EditPortsAsync(string id, IReadOnlyList<DockerPortBinding> ports, bool confirmed, ContainerSettingsEdit? settings = null)
    {
        await AdminAsync(settings == null ? "edit ports" : "edit container", id);
        ValidatePorts(ports);
        var name = (await InspectAsync(id))["Name"]!.ToString().TrimStart('/');
        var gate = Gate(name);
        await gate.WaitAsync();
        try
        {
            var node = await InspectAsync(id);
            var installation = await CheckOwnerAsync(node);
            if (node["Name"]!.ToString().TrimStart('/') != name) throw new InvalidOperationException("Container name changed. Refresh the list.");
            if (node["State"]?["Status"]?.ToString() is not ("running" or "exited" or "created"))
                throw new InvalidOperationException("Wait for the container state to settle before editing ports.");
            var running = node["State"]!["Running"]!.GetValue<bool>();
            if (running && !confirmed) throw new InvalidOperationException("Confirm the brief interruption before saving.");
            var snapshot = await inventory.ReadInventoryAsync();
            if (snapshot.Availability != DockerAvailability.Ready || SharesNamespace(snapshot,id,name))
                throw new InvalidOperationException("Could not verify an independent network namespace. Use the existing network workflow.");
            if (installation is Guid app)
            {
                if (settings != null) throw new InvalidOperationException("Use the Home Lab settings editor.");
                await homeLab.UpdatePublishedPortsAsync(app, id, ports);
                return;
            }
            var (reference, config) = await ReadConfigAsync(node, name);
            var original = config;
            var imageId = node["Image"]!.ToString();
            if (settings != null)
            {
                var values = new Dictionary<string,string>();
                foreach (var (key, value) in settings.Environment)
                    values[key] = value ?? (config.Environment.TryGetValue(key, out var existing) ? existing :
                        throw new InvalidOperationException("Enter a value for each new variable."));
                config = config with { Image = settings.Image, Volume = settings.Volume, MountPath = settings.MountPath,
                    Environment = new(HomeLabService.NormalizeContainerEnvironment(values)), Ports = ports };
                ValidateImageAndVolume(config);
                if (config.Image != original.Image)
                {
                    await RequireAsync(["pull", config.Image]);
                    imageId = await RequireAsync(["image", "inspect", "--format", "{{.Id}}", config.Image]);
                }
            }
            var mounts = new List<string>();
            foreach (var mount in node["Mounts"]!.AsArray())
            {
                if (mount?["Type"]?.ToString() != "volume") throw new InvalidOperationException("Use the existing manager for custom mounts.");
                if ((config.Volume != original.Volume || config.MountPath != original.MountPath) &&
                    mount["Name"]?.ToString() == original.Volume && mount["Destination"]?.ToString() == original.MountPath) continue;
                mounts.Add($"{mount["Name"]}:{mount["Destination"]}:{(mount["RW"]!.GetValue<bool>() ? "rw" : "ro")}");
            }
            if (config.Volume.Length > 0 && (config.Volume != original.Volume || config.MountPath != original.MountPath))
                mounts.Add(new ContainerVolume(config.Volume, config.MountPath, config.VolumeReadOnly).Binding);
            var network = node["HostConfig"]!["NetworkMode"]!.ToString();
            if (network is not ("bridge" or "default") || node["NetworkSettings"]!["Networks"]!.AsObject().Count != 1)
                throw new InvalidOperationException("Use the existing manager for custom networking.");
            var policy = node["HostConfig"]!["RestartPolicy"]!;
            var restart = policy["Name"]!.ToString();
            if (restart == "on-failure" && policy["MaximumRetryCount"]!.GetValue<int>() > 0) restart += ":" + policy["MaximumRetryCount"];
            if (!running)
            {
                var occupied = snapshot.Containers.Where(c => c.Running).SelectMany(c => c.Bindings);
                if (ports.Any(requested => occupied.Any(used => Conflicts(requested, used))))
                    throw new InvalidOperationException("The requested host port is occupied by another container.");
                CheckStoppedBindings(ports);
            }
            var nextReference = settings == null ? reference : await secrets.StoreSecretAsync(JsonSerializer.Serialize(config), Purpose(name));
            string candidate;
            try { candidate = await CreateAsync(config, nextReference, "lms-pending-" + Guid.NewGuid().ToString("N"), imageId, ports, mounts, restart, network); }
            catch
            {
                if (nextReference != reference) await secrets.DeleteSecretAsync(nextReference);
                throw;
            }
            var renamed = false;
            var stopped = false;
            try
            {
                var staged = await InspectAsync(candidate);
                if (staged["Image"]?.ToString() != imageId ||
                    !JsonNode.DeepEquals(staged["HostConfig"]?["RestartPolicy"], policy) ||
                    (settings == null ? !SameVolumes(node, staged) :
                        !mounts.All(binding => staged["Mounts"]!.AsArray().Any(m =>
                            $"{m?["Name"]}:{m?["Destination"]}:{(m?["RW"]?.GetValue<bool>() == true ? "rw" : "ro")}" == binding))))
                    throw new InvalidOperationException("The replacement does not match the original image, restart policy or volumes.");
                var current = await InspectAsync(id);
                if (await CheckOwnerAsync(current) != null || current["Name"]!.ToString() != "/" + name ||
                    current["State"]!["Running"]!.GetValue<bool>() != running || current["Config"]?["Labels"]?[ConfigLabel]?.ToString() != reference)
                    throw new InvalidOperationException("Container changed while preparing the replacement. Refresh and retry.");
                if (running)
                {
                    await RequireAsync(["stop", id]);
                    stopped = true;
                }
                await RequireAsync(["rename", id, BackupPrefix + id[..16] + (running ? "-running" : "-stopped")]);
                renamed = true;
                await RequireAsync(["rename", candidate, name]);
                if (running) await RequireAsync(["start", candidate]);
                await VerifyAsync(candidate, running, ports);
            }
            catch
            {
                await RequireAsync(["rm", "-f", candidate]);
                if (renamed) await RequireAsync(["rename", id, name]);
                if (stopped) await RequireAsync(["start", id]);
                if (nextReference != reference) await secrets.DeleteSecretAsync(nextReference);
                throw;
            }
            await RequireAsync(["rm", id]);
            if (nextReference != reference) await secrets.DeleteSecretAsync(reference);
        }
        finally { gate.Release(); }
    }
}
