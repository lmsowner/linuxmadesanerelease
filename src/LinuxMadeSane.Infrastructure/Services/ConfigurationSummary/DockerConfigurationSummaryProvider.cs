// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using System.Text.Json;
using LinuxMadeSane.Application.Contracts.SystemInfo;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services.ConfigurationSummary;

public sealed class DockerConfigurationSummaryProvider(ILinuxCommandRunner commandRunner)
    : ILmsConfigurationSummaryProvider, IDockerInventoryReader
{
    public async Task<IReadOnlyList<DockerContainerObservation>> GetContainersAsync(CancellationToken token = default)
    {
        var (status, rows) = await ReadRowsAsync(token);
        if (status is DockerAvailability.Ready or DockerAvailability.Missing) return rows;
        throw new InvalidOperationException($"Docker inventory unavailable ({status}).");
    }

    private async Task<DockerInventory> ReadRowsAsync(CancellationToken token)
    {
        string[] args = ["ps", "-a", "--no-trunc", "--format", "{{json .}}"];
        var result = await RunAsync(args, "List local Docker containers", false, token);
        if (result.ExitCode == 127) return new(DockerAvailability.Missing, []);
        if (result.ExitCode != 0)
        {
            var first = result.StandardError;
            result = await RunAsync(args, "List local Docker containers", true, token);
            if (result.ExitCode != 0)
            {
                bool Denied(string error) => error.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("sudo", StringComparison.OrdinalIgnoreCase) || error.Contains("access denied", StringComparison.OrdinalIgnoreCase);
                bool Down(string error) => error.Contains("cannot connect", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("connection refused", StringComparison.OrdinalIgnoreCase) || error.Contains("no such file", StringComparison.OrdinalIgnoreCase);
                return new(Down(result.StandardError) || Down(first) ? DockerAvailability.Stopped :
                    Denied(result.StandardError) || Denied(first) ? DockerAvailability.Denied : DockerAvailability.Failed, []);
            }
        }
        return new(DockerAvailability.Ready, ParseContainers(result.StandardOutput));
    }

    public async Task<DockerInventory> ReadInventoryAsync(CancellationToken token = default)
    {
        var inventory = await ReadRowsAsync(token);
        var rows = new List<DockerContainerObservation>();
        foreach (var batch in inventory.Containers.Chunk(100))
        {
            // Each address follows the port JSON as a tab-separated IP and quoted network name.
            const string networkAddresses = """{{range $name, $endpoint := .NetworkSettings.Networks}}{{with $endpoint.IPAddress}}{{printf "\t%s %s" . (json $name)}}{{end}}{{with $endpoint.GlobalIPv6Address}}{{printf "\t%s %s" . (json $name)}}{{end}}{{end}}""";
            string[] args = ["container", "inspect", "--format",
                "{{.Id}} {{.HostConfig.NetworkMode}} {{.HostConfig.RestartPolicy.Name}} {{if .State.Running}}{{json .NetworkSettings.Ports}}{{else}}{{json .HostConfig.PortBindings}}{{end}}" + networkAddresses,
                ..batch.Select(row => row.Id)];
            var result = await RunAsync(args, "Read Docker ports and network addresses", false, token);
            if (result.ExitCode != 0) result = await RunAsync(args, "Read Docker ports and network addresses", true, token);
            var containers = batch.ToDictionary(row => row.Id);
            foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                var fields = parts[0].Split(' ', 4);
                if (fields.Length != 4 || !containers.TryGetValue(fields[0], out var row)) continue;
                var addresses = new List<DockerNetworkAddress>();
                foreach (var part in parts.Skip(1))
                {
                    var separator = part.IndexOf(' ');
                    if (separator > 0 && JsonSerializer.Deserialize<string>(part[(separator + 1)..]) is string network)
                        addresses.Add(new(network, part[..separator]));
                }
                rows.Add(row with
                {
                    NetworkMode = fields[1], ManualStartup = fields[2] == "no",
                    Bindings = ParseBindings(fields[3]), Addresses = addresses
                });
            }
            if (result.ExitCode != 0 && !result.StandardError.Contains("No such", StringComparison.OrdinalIgnoreCase))
                return new(DockerAvailability.Failed, []);
        }
        return inventory with { Containers = rows };
    }

    internal static IReadOnlyList<DockerPortBinding> ParseBindings(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var ports = new List<DockerPortBinding>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return ports;
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var key = property.Name.Split('/');
            if (key.Length != 2 || !int.TryParse(key[0], out var target) || property.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in property.Value.EnumerateArray())
                if (int.TryParse(ReadString(item, "HostPort"), out var port))
                    ports.Add(new(port, target, key[1], ReadString(item, "HostIp") ?? ""));
        }
        return ports;
    }

    public string ModuleName => "Docker";

    public int SortOrder => 40;

    public async Task<LmsConfigurationSummary> GetConfigurationSummaryAsync(CancellationToken cancellationToken = default)
    {
        var version = await RunAsync(
            ["version", "--format", "{{.Server.Version}}"],
            "Read the Docker server version",
            requiresSudo: false,
            cancellationToken);
        if (version.ExitCode != 0 && version.ExitCode != 127)
        {
            version = await RunAsync(
                ["version", "--format", "{{.Server.Version}}"],
                "Read the Docker server version",
                requiresSudo: true,
                cancellationToken);
        }

        if (version.ExitCode == 127)
        {
            return Empty(LmsConfigurationSummaryStatus.NotConfigured);
        }

        if (version.ExitCode != 0)
        {
            return new LmsConfigurationSummary(
                ModuleName,
                LmsConfigurationSummaryStatus.Warning,
                "Local container runtime",
                [new("Status", "Installed but unavailable")],
                ["Docker is installed, but its service is stopped or inaccessible to LMS."],
                null,
                SortOrder);
        }

        var inventory = await ReadRowsAsync(cancellationToken);
        if (inventory.Availability != DockerAvailability.Ready)
        {
            return new LmsConfigurationSummary(
                ModuleName,
                LmsConfigurationSummaryStatus.Warning,
                "Local container runtime",
                [new("Status", "Running"), new("Version", version.StandardOutput.Trim())],
                ["Docker is running, but LMS could not list its containers."],
                null,
                SortOrder);
        }

        var containers = inventory.Containers;
        return new LmsConfigurationSummary(
            ModuleName,
            LmsConfigurationSummaryStatus.Configured,
            "Local container runtime",
            [
                new("Status", "Running"),
                new("Version", version.StandardOutput.Trim()),
                new("Containers", containers.Count.ToString()),
                new("Running", containers.Count(container => container.IsRunning).ToString()),
                new("Managed by LMS", containers.Count(container => container.Name.StartsWith("lms-", StringComparison.OrdinalIgnoreCase)).ToString()),
                new("Compose projects", containers.Select(container => container.ComposeProject).Where(value => value is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString())
            ],
            [],
            null,
            SortOrder);
    }

    internal static IReadOnlyList<DockerContainerObservation> ParseContainers(string output)
    {
        var containers = new List<DockerContainerObservation>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var name = ReadString(root, "Names") ?? ReadString(root, "Name") ?? string.Empty;
                var state = ReadString(root, "State") ?? string.Empty;
                var status = ReadString(root, "Status") ?? string.Empty;
                var labels = ReadString(root, "Labels") ?? string.Empty;
                var composeProject = labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault(label => label.StartsWith("com.docker.compose.project=", StringComparison.OrdinalIgnoreCase))?
                    .Split('=', 2)[1];
                containers.Add(new DockerContainerObservation(
                    name,
                    state.Equals("running", StringComparison.OrdinalIgnoreCase) || status.StartsWith("Up ", StringComparison.OrdinalIgnoreCase),
                    ReadString(root, "Ports") ?? "", ReadString(root, "Networks") ?? "")
                {
                    Id = ReadString(root, "ID") ?? "", State = state,
                    Image = labels.Split(',').FirstOrDefault(l => l.StartsWith("com.linuxmadesane.container.image=", StringComparison.Ordinal))?.Split('=', 2)[1]
                        ?? ReadString(root, "Image") ?? "",
                    SimpleConfig = labels.Split(',').FirstOrDefault(l => l.StartsWith("com.linuxmadesane.container.config=", StringComparison.Ordinal))?.Split('=', 2)[1],
                    HomeLabAppId = labels.Split(',').FirstOrDefault(l => l.StartsWith("com.linuxmadesane.homelab.app=", StringComparison.Ordinal))?.Split('=', 2)[1],
                    HomeLabLabel = labels.Split(',').Contains("com.linuxmadesane.homelab=true"),
                    ComposeProject = composeProject
                });
            }
            catch (JsonException)
            {
                // Ignore an individual malformed Docker row without discarding the useful rows.
            }
        }

        return containers;
    }

    private Task<LinuxCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        string description,
        bool requiresSudo,
        CancellationToken cancellationToken) =>
        commandRunner.RunAsync(
            new LinuxCommandRequest("docker", arguments, requiresSudo, TimeSpan.FromSeconds(6), description)
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

    private LmsConfigurationSummary Empty(LmsConfigurationSummaryStatus status) =>
        new(ModuleName, status, "Local container runtime", [], [], null, SortOrder);

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

}
