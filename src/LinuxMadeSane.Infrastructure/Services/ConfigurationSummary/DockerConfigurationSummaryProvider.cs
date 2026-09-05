// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;
using LinuxMadeSane.Application.Contracts.SystemInfo;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services.ConfigurationSummary;

public sealed class DockerConfigurationSummaryProvider(ILinuxCommandRunner commandRunner)
    : ILmsConfigurationSummaryProvider
{
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

        var containersResult = await RunAsync(
            ["ps", "-a", "--format", "{{json .}}"],
            "List Docker containers for the configuration summary",
            requiresSudo: false,
            cancellationToken);
        if (containersResult.ExitCode != 0)
        {
            containersResult = await RunAsync(
                ["ps", "-a", "--format", "{{json .}}"],
                "List Docker containers for the configuration summary",
                requiresSudo: true,
                cancellationToken);
        }

        if (containersResult.ExitCode != 0)
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

        var containers = ParseContainers(containersResult.StandardOutput);
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

    internal static IReadOnlyList<DockerContainerSummary> ParseContainers(string output)
    {
        var containers = new List<DockerContainerSummary>();
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
                containers.Add(new DockerContainerSummary(
                    name,
                    state.Equals("running", StringComparison.OrdinalIgnoreCase) || status.StartsWith("Up ", StringComparison.OrdinalIgnoreCase),
                    composeProject));
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

    internal sealed record DockerContainerSummary(string Name, bool IsRunning, string? ComposeProject);
}
