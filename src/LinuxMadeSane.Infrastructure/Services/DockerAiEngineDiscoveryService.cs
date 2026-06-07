// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.LocalAi;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class DockerAiEngineDiscoveryService(
    ILinuxCommandRunner commandRunner,
    IHttpClientFactory httpClientFactory) : IDockerAiEngineDiscoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DockerCommandTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DockerInstallTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    private static readonly IReadOnlyList<DockerAiEngineCatalogItem> Catalog =
    [
        new(
            "ollama",
            "Ollama",
            "Simple local model server with an OpenAI-compatible API and a large model catalog.",
            "ollama/ollama",
            "latest",
            "https://hub.docker.com/r/ollama/ollama",
            "https://ollama.com/blog/ollama-is-now-available-as-an-official-docker-image",
            "Official Docker image",
            DockerAiEngineApiProfile.OpenAiCompatible,
            11434,
            11434,
            "lms-ollama",
            "qwen2.5-coder:7b",
            false,
            false,
            "docker run -d --name lms-ollama -p 127.0.0.1:11434:11434 -v lms-ollama:/root/.ollama --restart unless-stopped ollama/ollama:latest",
            "Best default for most LMS users. Models can be pulled inside the container or managed by a Docker UI."),
        new(
            "localai",
            "LocalAI",
            "OpenAI-compatible local inference server for multiple model families and CPU-first deployments.",
            "localai/localai",
            "latest-cpu",
            "https://hub.docker.com/r/localai/localai",
            "https://localai.io/basics/container/",
            "Docker Sponsored OSS",
            DockerAiEngineApiProfile.OpenAiCompatible,
            8080,
            18080,
            "lms-localai",
            string.Empty,
            false,
            false,
            "docker run -d --name lms-localai -p 127.0.0.1:18080:8080 -v lms-localai-models:/models --restart unless-stopped localai/localai:latest-cpu",
            "Useful when the user wants an OpenAI-compatible local API without binding LMS to one runtime."),
        new(
            "vllm",
            "vLLM OpenAI server",
            "High-throughput OpenAI-compatible inference server for GPU-backed model serving.",
            "vllm/vllm-openai",
            "latest",
            "https://hub.docker.com/r/vllm/vllm-openai",
            "https://docs.vllm.ai/en/latest/deployment/docker.html",
            "Docker Sponsored OSS",
            DockerAiEngineApiProfile.OpenAiCompatible,
            8000,
            18000,
            "lms-vllm",
            "Qwen/Qwen3-0.6B",
            true,
            true,
            "docker run -d --name lms-vllm --gpus all -p 127.0.0.1:18000:8000 -v lms-vllm-cache:/root/.cache/huggingface --ipc=host --restart unless-stopped vllm/vllm-openai:latest --model Qwen/Qwen3-0.6B",
            "Use for serious local model serving on a host with a supported GPU and Docker GPU runtime.")
    ];

    public IReadOnlyList<DockerAiEngineCatalogItem> ListCatalog() => ResolveCatalog();

    public async Task<DockerAiEngineWorkspace> GetWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        var version = await RunDockerAsync(["--version"], "Detect Docker version", cancellationToken, allowSudoFallback: false);
        if (version.ExitCode != 0)
        {
            var docker = new DockerAiRuntimeStatus(
                false,
                false,
                false,
                string.Empty,
                "Docker is not installed or is not available to the LMS service account.",
                ["Install Docker before using Docker-hosted AI engines. A Docker manager such as Portainer is recommended for container lifecycle visibility."],
                DateTimeOffset.UtcNow);

            return new DockerAiEngineWorkspace(docker, ResolveCatalog(), []);
        }

        var info = await RunDockerAsync(["info", "--format", "{{json .}}"], "Inspect Docker daemon", cancellationToken);
        if (info.ExitCode != 0)
        {
            var docker = new DockerAiRuntimeStatus(
                true,
                false,
                false,
                version.StandardOutput.Trim(),
                "Docker is installed, but LMS cannot reach the Docker daemon.",
                [NormalizeDockerFailure(info), "Check Docker daemon status and whether the LMS service account is allowed to inspect local containers."],
                DateTimeOffset.UtcNow);

            return new DockerAiEngineWorkspace(docker, ResolveCatalog(), []);
        }

        var ps = await RunDockerAsync(["ps", "-a", "--format", "{{json .}}"], "Inspect Docker containers", cancellationToken);
        var containers = ps.ExitCode == 0
            ? ParseContainers(ps.StandardOutput)
            : [];
        var discovered = await DiscoverCompatibleEnginesAsync(containers, cancellationToken);
        var portainerDetected = containers.Any(container =>
            container.Image.Contains("portainer/portainer", StringComparison.OrdinalIgnoreCase) ||
            container.Name.Contains("portainer", StringComparison.OrdinalIgnoreCase));

        var warnings = new List<string>();
        if (!portainerDetected)
        {
            warnings.Add("Portainer or another Docker manager is recommended so model containers remain visible and easy to maintain.");
        }

        if (ps.ExitCode != 0)
        {
            warnings.Add(NormalizeDockerFailure(ps));
        }

        var status = new DockerAiRuntimeStatus(
            true,
            true,
            portainerDetected,
            version.StandardOutput.Trim(),
            portainerDetected
                ? "Docker is available and a Docker manager container was detected."
                : "Docker is available. Portainer was not detected.",
            warnings,
            DateTimeOffset.UtcNow);

        return new DockerAiEngineWorkspace(status, ResolveCatalog(), discovered);
    }

    public async Task<LocalAiApplyResult> InstallEngineAsync(
        string engineId,
        bool approved,
        CancellationToken cancellationToken = default)
    {
        if (!approved)
        {
            return Failed("Docker action blocked.", "Approve Docker engine actions before LMS installs or starts a container.", []);
        }

        var catalogItem = Catalog.FirstOrDefault(item => item.EngineId.Equals(engineId, StringComparison.OrdinalIgnoreCase));
        if (catalogItem is null)
        {
            return Failed("Docker engine not found.", "That trusted Docker AI engine is not in the LMS catalog.", []);
        }

        catalogItem = ResolveCatalogItem(catalogItem, new HashSet<int>());
        if (!catalogItem.IsHostPortAvailable)
        {
            return Failed(
                "Docker engine port unavailable.",
                $"{catalogItem.DisplayName} cannot be installed because LMS could not find an available local-only port. {catalogItem.HostPortDetail}",
                []);
        }

        var output = new List<string>();
        var existing = await RunDockerAsync(
            ["ps", "-a", "--filter", $"name=^/{catalogItem.SuggestedContainerName}$", "--format", "{{json .}}"],
            $"Inspect Docker AI container {catalogItem.SuggestedContainerName}",
            cancellationToken);
        AppendOutput(output, existing);

        if (existing.ExitCode == 0)
        {
            var container = ParseContainers(existing.StandardOutput).FirstOrDefault();
            if (container is not null)
            {
                if (IsContainerRunning(container))
                {
                    return new LocalAiApplyResult(
                        true,
                        "Docker engine already running.",
                        $"{catalogItem.DisplayName} is already running as {container.Name}.",
                        false,
                        output,
                        DateTimeOffset.UtcNow);
                }

                var startExisting = await RunDockerAsync(
                    ["start", container.Id],
                    $"Start Docker AI container {container.Name}",
                    cancellationToken);
                AppendOutput(output, startExisting);
                return startExisting.ExitCode == 0
                    ? new LocalAiApplyResult(
                        true,
                        "Docker engine started.",
                        $"{catalogItem.DisplayName} was already installed and has been started.",
                        false,
                        output,
                        DateTimeOffset.UtcNow)
                    : Failed("Docker engine start failed.", $"LMS could not start {catalogItem.DisplayName}.", output);
            }
        }

        var install = await RunDockerAsync(
            BuildInstallArguments(catalogItem),
            $"Install Docker AI engine {catalogItem.DisplayName}",
            cancellationToken,
            timeout: DockerInstallTimeout);
        AppendOutput(output, install);

        return install.ExitCode == 0
            ? new LocalAiApplyResult(
                true,
                "Docker engine installed.",
                $"{catalogItem.DisplayName} is installed and starting. Refresh the page once the container is ready, then add it as an AI provider after at least one model is available.",
                false,
                output,
                DateTimeOffset.UtcNow)
            : Failed("Docker engine install failed.", $"LMS could not install {catalogItem.DisplayName}.", output);
    }

    public async Task<LocalAiApplyResult> StartContainerAsync(
        string containerIdOrName,
        bool approved,
        CancellationToken cancellationToken = default) =>
        await RunContainerActionAsync(
            "start",
            containerIdOrName,
            approved,
            "Docker engine started.",
            "LMS could not start the Docker AI engine.",
            cancellationToken);

    public async Task<LocalAiApplyResult> StopContainerAsync(
        string containerIdOrName,
        bool approved,
        CancellationToken cancellationToken = default) =>
        await RunContainerActionAsync(
            "stop",
            containerIdOrName,
            approved,
            "Docker engine stopped.",
            "LMS could not stop the Docker AI engine.",
            cancellationToken);

    private async Task<LocalAiApplyResult> RunContainerActionAsync(
        string action,
        string containerIdOrName,
        bool approved,
        string successSummary,
        string failureDetail,
        CancellationToken cancellationToken)
    {
        if (!approved)
        {
            return Failed("Docker action blocked.", "Approve Docker engine actions before LMS changes a container.", []);
        }

        if (string.IsNullOrWhiteSpace(containerIdOrName))
        {
            return Failed("Docker container missing.", "LMS did not receive a Docker container name or ID.", []);
        }

        var result = await RunDockerAsync(
            [action, containerIdOrName.Trim()],
            $"{action} Docker AI container {containerIdOrName}",
            cancellationToken);
        var output = new List<string>();
        AppendOutput(output, result);

        return result.ExitCode == 0
            ? new LocalAiApplyResult(
                true,
                successSummary,
                $"{containerIdOrName.Trim()} is now {action switch { "start" => "running", "stop" => "stopped", _ => "updated" }}.",
                false,
                output,
                DateTimeOffset.UtcNow)
            : Failed($"{successSummary.TrimEnd('.')} failed.", failureDetail, output);
    }

    private async Task<IReadOnlyList<DockerAiDiscoveredEngine>> DiscoverCompatibleEnginesAsync(
        IReadOnlyList<DockerContainerRow> containers,
        CancellationToken cancellationToken)
    {
        var engines = new List<DockerAiDiscoveredEngine>();

        foreach (var container in containers)
        {
            var publishedPorts = ParsePublishedTcpPorts(container.Ports);
            if (publishedPorts.Count == 0)
            {
                publishedPorts = await InspectPublishedTcpPortsAsync(container.Id, cancellationToken);
            }

            var matchedCatalog = MatchCatalog(container.Image, 0);
            if (publishedPorts.Count == 0 && matchedCatalog is not null)
            {
                engines.Add(BuildStoppedCatalogEngine(container, matchedCatalog));
                continue;
            }

            foreach (var publishedPort in publishedPorts)
            {
                var catalogItem = MatchCatalog(container.Image, publishedPort.ContainerPort);
                if (catalogItem is null && !IsLikelyOpenAiCompatiblePort(publishedPort.ContainerPort))
                {
                    continue;
                }

                var engineId = catalogItem?.EngineId ?? "openai-compatible";
                var isRunning = IsContainerRunning(container);
                var endpointRoot = $"http://{ResolveProbeHost(publishedPort.HostIp)}:{publishedPort.HostPort}";
                var probe = isRunning
                    ? await ProbeModelsAsync(endpointRoot, cancellationToken)
                    : new DockerAiModelProbe(false, string.Empty, []);
                if (isRunning && !probe.IsReachable && catalogItem is null)
                {
                    continue;
                }

                var displayName = catalogItem?.DisplayName ?? "OpenAI-compatible container";
                engines.Add(new DockerAiDiscoveredEngine(
                    engineId,
                    container.Id,
                    container.Name,
                    container.Image,
                    container.Status,
                    $"{endpointRoot.TrimEnd('/')}/v1",
                    probe.DefaultModelId,
                    probe.ModelIds,
                    isRunning,
                    probe.IsReachable,
                    !isRunning
                        ? $"{displayName} is installed as {container.Name}, but the container is stopped."
                        : probe.IsReachable
                        ? $"{displayName} is reachable through {endpointRoot}."
                        : $"{displayName} is published on {endpointRoot}, but LMS could not list models yet."));
            }
        }

        return engines
            .DistinctBy(item => $"{item.ContainerId}:{item.BaseUrl}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.ContainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.BaseUrl, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<DockerPublishedPort>> InspectPublishedTcpPortsAsync(
        string containerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            return [];
        }

        var inspect = await RunDockerAsync(
            ["inspect", containerId.Trim(), "--format", "{{json .NetworkSettings.Ports}}"],
            $"Inspect Docker port bindings for {containerId}",
            cancellationToken);

        return inspect.ExitCode == 0
            ? ParseInspectedPublishedTcpPorts(inspect.StandardOutput)
            : [];
    }

    private static DockerAiDiscoveredEngine BuildStoppedCatalogEngine(
        DockerContainerRow container,
        DockerAiEngineCatalogItem catalogItem)
    {
        var endpointRoot = $"http://127.0.0.1:{catalogItem.HostPort}";
        return new DockerAiDiscoveredEngine(
            catalogItem.EngineId,
            container.Id,
            container.Name,
            container.Image,
            container.Status,
            $"{endpointRoot}/v1",
            string.Empty,
            [],
            false,
            false,
            $"{catalogItem.DisplayName} is installed as {container.Name}, but the container is stopped. Start it to probe the model API.");
    }

    private async Task<DockerAiModelProbe> ProbeModelsAsync(string endpointRoot, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            using var httpClient = httpClientFactory.CreateClient();
            using var response = await httpClient.GetAsync($"{endpointRoot.TrimEnd('/')}/v1/models", timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return await ProbeOllamaTagsAsync(endpointRoot, timeoutCts.Token);
            }

            var modelIds = ParseOpenAiModelIds(body);
            return new DockerAiModelProbe(
                true,
                modelIds.FirstOrDefault() ?? string.Empty,
                modelIds);
        }
        catch
        {
            return await ProbeOllamaTagsAsync(endpointRoot, cancellationToken);
        }
    }

    private async Task<DockerAiModelProbe> ProbeOllamaTagsAsync(string endpointRoot, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            using var httpClient = httpClientFactory.CreateClient();
            using var response = await httpClient.GetAsync($"{endpointRoot.TrimEnd('/')}/api/tags", timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new DockerAiModelProbe(false, string.Empty, []);
            }

            var modelIds = ParseOllamaModelIds(body);
            return new DockerAiModelProbe(
                true,
                modelIds.FirstOrDefault() ?? string.Empty,
                modelIds);
        }
        catch
        {
            return new DockerAiModelProbe(false, string.Empty, []);
        }
    }

    private static IReadOnlyList<string> ParseOpenAiModelIds(string body)
    {
        try
        {
            var data = JsonNode.Parse(body)?["data"] as JsonArray ?? [];
            return data
                .OfType<JsonObject>()
                .Select(item => item["id"]?.GetValue<string>() ?? string.Empty)
                .Where(IsLikelyTextModel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ParseOllamaModelIds(string body)
    {
        try
        {
            var models = JsonNode.Parse(body)?["models"] as JsonArray ?? [];
            return models
                .OfType<JsonObject>()
                .Select(item => item["name"]?.GetValue<string>() ?? string.Empty)
                .Where(IsLikelyTextModel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool IsLikelyTextModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var normalized = modelId.Trim().ToLowerInvariant();
        return !IsAuxiliaryModelArtifact(normalized) &&
               !normalized.Contains("audio", StringComparison.Ordinal) &&
               !normalized.Contains("embedding", StringComparison.Ordinal) &&
               !normalized.Contains("image", StringComparison.Ordinal) &&
               !normalized.Contains("moderation", StringComparison.Ordinal) &&
               !normalized.Contains("rerank", StringComparison.Ordinal) &&
               !normalized.Contains("tts", StringComparison.Ordinal) &&
               !normalized.Contains("whisper", StringComparison.Ordinal);
    }

    private static bool IsAuxiliaryModelArtifact(string normalizedModelId)
    {
        var fileName = normalizedModelId.Replace('\\', '/').Split('/').LastOrDefault() ?? normalizedModelId;
        return fileName.StartsWith("mmproj", StringComparison.Ordinal) ||
               fileName.Contains("-mmproj", StringComparison.Ordinal) ||
               fileName.Contains("_mmproj", StringComparison.Ordinal) ||
               fileName.EndsWith(".mmproj", StringComparison.Ordinal) ||
               (fileName.Contains("projector", StringComparison.Ordinal) &&
                fileName.EndsWith(".gguf", StringComparison.Ordinal));
    }

    private static DockerAiEngineCatalogItem? MatchCatalog(string image, int containerPort)
    {
        var normalizedImage = image.Split('@')[0].Split(':')[0];
        return Catalog.FirstOrDefault(item =>
                   normalizedImage.Equals(item.DockerImage, StringComparison.OrdinalIgnoreCase)) ??
               Catalog.FirstOrDefault(item => item.ContainerPort == containerPort);
    }

    private static bool IsLikelyOpenAiCompatiblePort(int containerPort) =>
        containerPort is 8000 or 8080 or 11434 or 1234;

    private static IReadOnlyList<DockerAiEngineCatalogItem> ResolveCatalog()
    {
        var reservedPorts = new HashSet<int>();
        return Catalog
            .Select(item => ResolveCatalogItem(item, reservedPorts))
            .ToArray();
    }

    private static DockerAiEngineCatalogItem ResolveCatalogItem(
        DockerAiEngineCatalogItem item,
        ISet<int> reservedPorts)
    {
        var preferredPorts = GetPreferredHostPorts(item);
        foreach (var port in preferredPorts)
        {
            if (reservedPorts.Contains(port) || !IsLoopbackPortAvailable(port))
            {
                continue;
            }

            reservedPorts.Add(port);
            var defaultPort = preferredPorts[0];
            var detail = port == defaultPort
                ? $"LMS checked 127.0.0.1:{port}; it is available for a local-only Docker bind."
                : $"LMS checked the preferred ports. 127.0.0.1:{defaultPort} is not available, so LMS will use 127.0.0.1:{port}.";

            return item with
            {
                HostPort = port,
                RunCommand = BuildRunCommand(item, port),
                IsHostPortAvailable = true,
                HostPortDetail = detail
            };
        }

        var fallbackPort = preferredPorts[0];
        return item with
        {
            HostPort = fallbackPort,
            RunCommand = $"# No safe local-only port is currently available for {item.DisplayName}.{Environment.NewLine}# Free one of: {string.Join(", ", preferredPorts.Select(port => $"127.0.0.1:{port}"))}",
            IsHostPortAvailable = false,
            HostPortDetail = $"No preferred local-only port is available. Checked: {string.Join(", ", preferredPorts.Select(port => $"127.0.0.1:{port}"))}."
        };
    }

    private static IReadOnlyList<int> GetPreferredHostPorts(DockerAiEngineCatalogItem item) =>
        item.EngineId switch
        {
            "ollama" => [11434, 11435, 11436, 11437, 11438],
            "localai" => [18080, 18081, 18082, 18083, 18084],
            "vllm" => [18000, 18001, 18002, 18003, 18004],
            _ => [item.HostPort, item.HostPort + 1, item.HostPort + 2]
        };

    private static bool IsLoopbackPortAvailable(int port)
    {
        if (port is <= 0 or > 65535)
        {
            return false;
        }

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static string BuildRunCommand(DockerAiEngineCatalogItem item, int hostPort) =>
        string.Join(" ", BuildInstallArguments(item with { HostPort = hostPort }).Select(QuoteForPreview)).Insert(0, "docker ");

    private static string QuoteForPreview(string value) =>
        value.Length == 0
            ? "''"
            : value.Any(char.IsWhiteSpace) || value.Contains('"') || value.Contains('\'')
                ? $"'{value.Replace("'", "'\"'\"'")}'"
                : value;

    private async Task<LinuxCommandResult> RunDockerAsync(
        IReadOnlyList<string> arguments,
        string description,
        CancellationToken cancellationToken,
        bool allowSudoFallback = true,
        TimeSpan? timeout = null)
    {
        var result = await commandRunner.RunAsync(
            BuildDockerCommand(arguments, description, requiresSudo: false, timeout ?? DockerCommandTimeout),
            dryRun: false,
            cancellationToken);

        if (!allowSudoFallback || result.ExitCode == 0 || !IsDockerSocketPermissionDenied(result))
        {
            return result;
        }

        return await commandRunner.RunAsync(
            BuildDockerCommand(arguments, description, requiresSudo: true, timeout ?? DockerCommandTimeout),
            dryRun: false,
            cancellationToken);
    }

    private static LinuxCommandRequest BuildDockerCommand(
        IReadOnlyList<string> arguments,
        string description,
        bool requiresSudo,
        TimeSpan timeout) =>
        new("docker", arguments, requiresSudo, timeout, description)
        {
            IsOptionalExternalTool = true
        };

    private static IReadOnlyList<string> BuildInstallArguments(DockerAiEngineCatalogItem item) =>
        item.EngineId switch
        {
            "ollama" =>
            [
                "run", "-d",
                "--name", item.SuggestedContainerName,
                "-p", $"{ResolveDockerBindAddress(item.HostPort)}:{item.ContainerPort}",
                "-v", "lms-ollama:/root/.ollama",
                "--restart", "unless-stopped",
                $"{item.DockerImage}:{item.DockerTag}"
            ],
            "localai" =>
            [
                "run", "-d",
                "--name", item.SuggestedContainerName,
                "-p", $"{ResolveDockerBindAddress(item.HostPort)}:{item.ContainerPort}",
                "-v", "lms-localai-models:/models",
                "--restart", "unless-stopped",
                $"{item.DockerImage}:{item.DockerTag}"
            ],
            "vllm" =>
            [
                "run", "-d",
                "--name", item.SuggestedContainerName,
                "--gpus", "all",
                "-p", $"{ResolveDockerBindAddress(item.HostPort)}:{item.ContainerPort}",
                "-v", "lms-vllm-cache:/root/.cache/huggingface",
                "--ipc=host",
                "--restart", "unless-stopped",
                $"{item.DockerImage}:{item.DockerTag}",
                "--model", item.DefaultModelId
            ],
            _ =>
            [
                "run", "-d",
                "--name", item.SuggestedContainerName,
                "-p", $"{ResolveDockerBindAddress(item.HostPort)}:{item.ContainerPort}",
                "--restart", "unless-stopped",
                $"{item.DockerImage}:{item.DockerTag}"
            ]
        };

    private static string ResolveDockerBindAddress(int hostPort) => $"127.0.0.1:{hostPort}";

    private static IReadOnlyList<DockerContainerRow> ParseContainers(string stdout) =>
        stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseContainer)
            .Where(item => item is not null)
            .Cast<DockerContainerRow>()
            .ToArray();

    private static DockerContainerRow? ParseContainer(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return new DockerContainerRow(
                GetJsonString(root, "ID"),
                GetJsonString(root, "Names"),
                GetJsonString(root, "Image"),
                GetJsonString(root, "Ports"),
                GetJsonString(root, "Status"),
                GetJsonString(root, "State"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsContainerRunning(DockerContainerRow container) =>
        container.State.Equals("running", StringComparison.OrdinalIgnoreCase) ||
        container.Status.StartsWith("Up ", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<DockerPublishedPort> ParsePublishedTcpPorts(string ports)
    {
        if (string.IsNullOrWhiteSpace(ports))
        {
            return [];
        }

        var results = new List<DockerPublishedPort>();
        foreach (var segment in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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

            var containerPort = ParseFirstPort(parts[1].Split('/')[0]);
            var hostSide = parts[0].Trim();
            var lastColon = hostSide.LastIndexOf(':');
            if (lastColon < 0 ||
                !int.TryParse(hostSide[(lastColon + 1)..], out var hostPort) ||
                hostPort <= 0 ||
                containerPort <= 0)
            {
                continue;
            }

            var hostIp = hostSide[..lastColon].Trim().Trim('[', ']');
            results.Add(new DockerPublishedPort(
                string.IsNullOrWhiteSpace(hostIp) ? "127.0.0.1" : hostIp,
                hostPort,
                containerPort));
        }

        return results;
    }

    private static IReadOnlyList<DockerPublishedPort> ParseInspectedPublishedTcpPorts(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(stdout.Trim());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var results = new List<DockerPublishedPort>();
            foreach (var portProperty in document.RootElement.EnumerateObject())
            {
                var nameParts = portProperty.Name.Split('/', 2, StringSplitOptions.TrimEntries);
                if (nameParts.Length != 2 ||
                    !nameParts[1].Equals("tcp", StringComparison.OrdinalIgnoreCase) ||
                    !int.TryParse(nameParts[0], out var containerPort) ||
                    containerPort <= 0 ||
                    portProperty.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var binding in portProperty.Value.EnumerateArray())
                {
                    var hostPortText = GetJsonString(binding, "HostPort");
                    if (!int.TryParse(hostPortText, out var hostPort) || hostPort <= 0)
                    {
                        continue;
                    }

                    var hostIp = GetJsonString(binding, "HostIp");
                    results.Add(new DockerPublishedPort(
                        string.IsNullOrWhiteSpace(hostIp) ? "127.0.0.1" : hostIp,
                        hostPort,
                        containerPort));
                }
            }

            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static int ParseFirstPort(string value)
    {
        var portText = value.Split('-', 2, StringSplitOptions.TrimEntries)[0];
        return int.TryParse(portText, out var port) ? port : 0;
    }

    private static string ResolveProbeHost(string hostIp) =>
        string.IsNullOrWhiteSpace(hostIp) ||
        hostIp.Equals("0.0.0.0", StringComparison.Ordinal) ||
        hostIp.Equals("::", StringComparison.Ordinal) ||
        hostIp.Equals("*", StringComparison.Ordinal)
            ? "127.0.0.1"
            : hostIp.Trim();

    private static string NormalizeDockerFailure(LinuxCommandResult result)
    {
        if (IsDockerSocketPermissionDenied(result))
        {
            return result.CommandText.StartsWith("sudo -n ", StringComparison.Ordinal)
                ? "LMS could not use its local privileged runner to inspect Docker. Rerun the latest LMS installer/update so the LMS service account has local system access, then restart LMS."
                : "LMS can see Docker, but the LMS service account is not allowed to inspect the Docker daemon.";
        }

        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;

        if (result.CommandText.StartsWith("sudo -n docker ", StringComparison.Ordinal) &&
            (detail.Contains("password", StringComparison.OrdinalIgnoreCase) ||
             detail.Contains("not allowed", StringComparison.OrdinalIgnoreCase) ||
             detail.Contains("sudo", StringComparison.OrdinalIgnoreCase)))
        {
            return "LMS cannot use its local privileged runner to inspect Docker. Rerun the latest LMS installer/update so the LMS service account has local system access, then restart LMS.";
        }

        return string.IsNullOrWhiteSpace(detail)
            ? "Docker reported a problem without details."
            : detail.Trim();
    }

    private static bool IsDockerSocketPermissionDenied(LinuxCommandResult result)
    {
        var output = $"{result.StandardError}\n{result.StandardOutput}";
        return output.Contains("/var/run/docker.sock", StringComparison.OrdinalIgnoreCase) &&
               output.Contains("permission denied", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendOutput(List<string> output, LinuxCommandResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.CommandText))
        {
            output.Add($"> {result.CommandText}");
        }

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            output.Add(result.StandardOutput.Trim());
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            output.Add(result.StandardError.Trim());
        }
    }

    private static LocalAiApplyResult Failed(string summary, string detail, IReadOnlyList<string> output) =>
        new(false, summary, detail, false, output, DateTimeOffset.UtcNow);

    private static string GetJsonString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private sealed record DockerContainerRow(
        string Id,
        string Name,
        string Image,
        string Ports,
        string Status,
        string State);

    private sealed record DockerPublishedPort(
        string HostIp,
        int HostPort,
        int ContainerPort);

    private sealed record DockerAiModelProbe(
        bool IsReachable,
        string DefaultModelId,
        IReadOnlyList<string> ModelIds);
}
