// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;
using System.Text.Json.Nodes;
using LinuxMadeSane.Application.Contracts.EdgeGateway;
using LinuxMadeSane.Application.Contracts.HomeLab;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.HomeLab;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using LinuxMadeSane.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class HomeLabService(
    LinuxMadeSaneDbContext dbContext,
    ILinuxCommandRunner commandRunner,
    IEdgeGatewayService edgeGatewayService,
    HomeLabStorageOptions storageOptions,
    ILogger<HomeLabService> logger) : IHomeLabService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DockerCommandTimeout = TimeSpan.FromMinutes(5);

    public async Task<HomeLabWorkspace> GetWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.HomeLabInstallations
            .OrderBy(item => item.DisplayName)
            .ToListAsync(cancellationToken);

        foreach (var entity in entities)
        {
            await RefreshHealthInternalAsync(entity, cancellationToken);
        }

        if (entities.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var installations = entities.Select(MapInstallation).ToArray();
        var deploymentEntities = await dbContext.HomeLabDeployments
            .AsNoTracking()
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
        var deployments = deploymentEntities.Select(MapDeployment).ToArray();
        var storageRoleEntities = await dbContext.HomeLabStorageRoles
            .AsNoTracking()
            .OrderBy(item => item.Role)
            .ToListAsync(cancellationToken);
        var storageRoles = storageRoleEntities.Select(MapStorageRole).ToArray();

        return new HomeLabWorkspace(HomeLabCatalog.Apps, HomeLabCatalog.Recipes, storageRoles, deployments, installations);
    }

    public async Task<HomeLabStorageRole> SaveStorageRoleAsync(
        string role,
        string hostPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedRole = NormalizeRole(role);
        var normalizedPath = NormalizeHostPath(hostPath);
        var directory = await RunAsync(
            new LinuxCommandRequest(
                "mkdir",
                ["-p", normalizedPath],
                true,
                TimeSpan.FromSeconds(30),
                $"Prepare Home Lab storage role {normalizedRole}"),
            cancellationToken);
        EnsureSuccess(directory, $"Storage role '{normalizedRole}' could not be prepared.");

        var entity = await dbContext.HomeLabStorageRoles.FindAsync([normalizedRole], cancellationToken);
        if (entity is null)
        {
            entity = new HomeLabStorageRoleEntity { Role = normalizedRole };
            dbContext.HomeLabStorageRoles.Add(entity);
        }

        entity.HostPath = normalizedPath;
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapStorageRole(entity);
    }

    public Task<HomeLabOperationResult> InstallAppAsync(
        HomeLabInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InstallDeploymentAsync(
            request.AppId,
            request.DisplayName,
            request.StoragePaths,
            request.Configuration,
            request.EdgeGateway,
            null,
            null,
            cancellationToken);
    }

    public Task<HomeLabOperationResult> InstallRecipeAsync(
        HomeLabRecipeInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InstallDeploymentAsync(
            request.RecipeId,
            null,
            request.StoragePaths,
            request.Configuration,
            null,
            request.RecipeId,
            request.AppIds,
            cancellationToken);
    }

    public async Task<HomeLabOperationResult> ExecuteAsync(
        Guid installationId,
        HomeLabLifecycleAction action,
        CancellationToken cancellationToken = default)
    {
        var installation = await dbContext.HomeLabInstallations
            .SingleOrDefaultAsync(item => item.Id == installationId, cancellationToken)
            ?? throw new InvalidOperationException("The Home Lab installation was not found.");
        var app = HomeLabCatalog.GetApp(installation.AppId);
        var output = new List<string>();

        if (action == HomeLabLifecycleAction.Remove)
        {
            var remove = await RunDockerAsync(
                ["rm", "--force", installation.ContainerName],
                $"Remove Home Lab container {installation.ContainerName}",
                cancellationToken);
            AppendOutput(output, remove);
            if (remove.ExitCode != 0 && !ContainsNoSuchContainer(remove))
            {
                return Failure("Home Lab app removal failed.", NormalizeFailure(remove), output, HomeLabHealthState.Failed);
            }

            var deployment = await dbContext.HomeLabDeployments
                .SingleAsync(item => item.Id == installation.DeploymentId, cancellationToken);
            var isLast = !await dbContext.HomeLabInstallations.AnyAsync(
                item => item.DeploymentId == deployment.Id && item.Id != installation.Id,
                cancellationToken);
            var routeId = installation.EdgeGatewayRouteId;
            dbContext.HomeLabInstallations.Remove(installation);
            if (isLast)
            {
                var network = await RunDockerAsync(
                    ["network", "rm", deployment.NetworkName],
                    $"Remove Home Lab network {deployment.NetworkName}",
                    cancellationToken);
                AppendOutput(output, network);
                dbContext.HomeLabDeployments.Remove(deployment);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            if (routeId.HasValue)
            {
                await edgeGatewayService.DeleteRouteAsync(routeId.Value, cancellationToken);
            }

            return Success(
                "Home Lab app removed.",
                $"{app.Name} was removed. Persistent host data was left in place.",
                output,
                HomeLabHealthState.Stopped);
        }

        if (action == HomeLabLifecycleAction.Update)
        {
            var pull = await RunDockerAsync(
                ["pull", installation.Image],
                $"Pull updated Home Lab image {installation.Image}",
                cancellationToken);
            AppendOutput(output, pull);
            if (pull.ExitCode != 0)
            {
                return Failure("Home Lab image update failed.", NormalizeFailure(pull), output, HomeLabHealthState.Failed);
            }

            var remove = await RunDockerAsync(
                ["rm", "--force", installation.ContainerName],
                $"Replace Home Lab container {installation.ContainerName}",
                cancellationToken);
            AppendOutput(output, remove);
            if (remove.ExitCode != 0 && !ContainsNoSuchContainer(remove))
            {
                return Failure("Home Lab container replacement failed.", NormalizeFailure(remove), output, HomeLabHealthState.Failed);
            }

            var run = await RunContainerAsync(
                installation,
                app,
                DeserializeBindings(installation.VolumeMappingsJson),
                DeserializeDictionary(installation.ConfigurationJson),
                output,
                cancellationToken);
            if (!run.Succeeded)
            {
                return run.Result!;
            }

            installation.PortMappingsJson = JsonSerializer.Serialize(run.PortBindings, JsonOptions);
            installation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await RefreshHealthInternalAsync(installation, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Success(
                "Home Lab app updated.",
                $"{app.Name} was recreated from {installation.Image}.",
                output,
                ToHealth(installation.HealthState));
        }

        var actionName = action switch
        {
            HomeLabLifecycleAction.Start => "start",
            HomeLabLifecycleAction.Stop => "stop",
            HomeLabLifecycleAction.Restart => "restart",
            HomeLabLifecycleAction.RefreshHealth => null,
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        if (actionName is not null)
        {
            var result = await RunDockerAsync(
                [actionName, installation.ContainerName],
                $"{actionName} Home Lab container {installation.ContainerName}",
                cancellationToken);
            AppendOutput(output, result);
            if (result.ExitCode != 0)
            {
                installation.HealthState = (int)HomeLabHealthState.Failed;
                installation.HealthDetail = NormalizeFailure(result);
                installation.UpdatedAtUtc = DateTimeOffset.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                return Failure(
                    $"Home Lab app {actionName} failed.",
                    installation.HealthDetail,
                    output,
                    HomeLabHealthState.Failed);
            }
        }

        await RefreshHealthInternalAsync(installation, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Success(
            action == HomeLabLifecycleAction.RefreshHealth ? "Home Lab health refreshed." : $"Home Lab app {actionName}ed.",
            installation.HealthDetail,
            output,
            ToHealth(installation.HealthState));
    }

    public async Task<HomeLabLogsResult> GetLogsAsync(Guid installationId, CancellationToken cancellationToken = default)
    {
        var installation = await dbContext.HomeLabInstallations
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == installationId, cancellationToken);
        if (installation is null)
        {
            return new HomeLabLogsResult(false, string.Empty, "The Home Lab installation was not found.");
        }

        var result = await RunDockerAsync(
            ["logs", "--tail", "200", installation.ContainerName],
            $"Read Home Lab logs for {installation.ContainerName}",
            cancellationToken);
        return new HomeLabLogsResult(
            result.ExitCode == 0,
            result.StandardOutput,
            result.ExitCode == 0 ? string.Empty : NormalizeFailure(result));
    }

    public async Task<HomeLabEffectiveConfiguration?> GetEffectiveConfigurationAsync(
        Guid deploymentId,
        CancellationToken cancellationToken = default)
    {
        var deployment = await dbContext.HomeLabDeployments
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == deploymentId, cancellationToken);
        if (deployment is null)
        {
            return null;
        }

        var installations = await dbContext.HomeLabInstallations
            .AsNoTracking()
            .Where(item => item.DeploymentId == deploymentId)
            .OrderBy(item => item.AppId)
            .ToListAsync(cancellationToken);

        return new HomeLabEffectiveConfiguration(
            deployment.Id,
            deployment.NetworkName,
            installations.Select(item => new HomeLabEffectiveContainer(
                item.AppId,
                item.ContainerName,
                item.Image,
                DeserializeBindings(item.VolumeMappingsJson)
                    .Select(binding => $"{binding.HostPath}:{binding.ContainerPath}{(binding.ReadOnly ? ":ro" : string.Empty)}")
                    .ToArray(),
                DeserializePortBindings(item.PortMappingsJson)
                    .Select(binding => $"127.0.0.1:{binding.HostPort}:{binding.ContainerPort}")
                    .ToArray(),
                HomeLabCatalog.GetApp(item.AppId).Dependencies)).ToArray());
    }

    private async Task<HomeLabOperationResult> InstallDeploymentAsync(
        string id,
        string? displayName,
        IReadOnlyDictionary<string, string>? storagePaths,
        IReadOnlyDictionary<string, string>? configuration,
        HomeLabEdgeGatewayRequest? edgeGateway,
        string? recipeId,
        IReadOnlySet<string>? selectedAppIds,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> appIds = recipeId is null
            ? [id]
            : HomeLabCatalog.GetRecipe(recipeId).AppIds
                .Where(appId => selectedAppIds is null || selectedAppIds.Contains(appId, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        if (appIds.Count == 0)
        {
            throw new InvalidOperationException("Choose at least one Home Lab app.");
        }

        if (recipeId is not null && !HomeLabCatalog.GetRecipe(recipeId).IsInstallable)
        {
            throw new InvalidOperationException("This recipe is catalogued for a later Home Lab phase and is not installable yet.");
        }

        var apps = appIds.Select(HomeLabCatalog.GetApp).ToArray();
        if (apps.Any(app => !app.IsInstallable))
        {
            var unavailable = string.Join(", ", apps.Where(app => !app.IsInstallable).Select(app => app.Name));
            throw new InvalidOperationException($"These catalog entries are not installable in this Home Lab phase: {unavailable}.");
        }

        var deploymentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var networkName = BuildNetworkName(deploymentId);
        var deployment = new HomeLabDeploymentEntity
        {
            Id = deploymentId,
            Name = displayName?.Trim() is { Length: > 0 } name
                ? name
                : recipeId is null ? apps[0].Name : HomeLabCatalog.GetRecipe(recipeId).Name,
            RecipeId = recipeId,
            NetworkName = networkName,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var output = new List<string>();
        var createdContainers = new List<string>();
        var createdRoutes = new List<Guid>();

        try
        {
            var network = await EnsureNetworkAsync(networkName, cancellationToken);
            output.AddRange(network.Output);
            if (!network.Succeeded)
            {
                throw new InvalidOperationException(network.Detail);
            }

            foreach (var app in apps)
            {
                var installation = BuildInstallationEntity(
                    deployment,
                    app,
                    storagePaths,
                    configuration,
                    recipeId is not null,
                    now);
                var run = await RunContainerAsync(
                    installation,
                    app,
                    DeserializeBindings(installation.VolumeMappingsJson),
                    DeserializeDictionary(installation.ConfigurationJson),
                    output,
                    cancellationToken);
                if (!run.Succeeded)
                {
                    throw new InvalidOperationException(run.Result?.Detail ?? "The Home Lab container could not be created.");
                }

                createdContainers.Add(installation.ContainerName);
                installation.PortMappingsJson = JsonSerializer.Serialize(run.PortBindings, JsonOptions);
                await RefreshHealthInternalAsync(installation, cancellationToken);

                if (edgeGateway is not null && app.Id == apps[0].Id)
                {
                    var primaryPort = run.PortBindings.FirstOrDefault(port =>
                        port.Name.Equals(ResolvePrimaryPort(app).Name, StringComparison.OrdinalIgnoreCase));
                    if (primaryPort is null)
                    {
                        throw new InvalidOperationException($"{app.Name} did not publish a primary web port for Edge Gateway routing.");
                    }

                    installation.EdgeGatewayRouteId = await edgeGatewayService.SaveRouteAsync(
                        new EdgeGatewayRouteEditor
                        {
                            DisplayName = app.Name,
                            Hostname = edgeGateway.Hostname,
                            DomainName = edgeGateway.DomainName,
                            AuthMode = edgeGateway.AuthMode,
                            TargetScheme = EdgeGatewayTargetScheme.Http,
                            TargetHost = "127.0.0.1",
                            TargetPort = primaryPort.HostPort,
                            UsePublicHostHeader = true,
                            StripForwardedFor = true,
                            SkipUpstreamTlsVerification = true,
                            Notes = $"Home Lab app: {app.Id}"
                        },
                        cancellationToken);
                    createdRoutes.Add(installation.EdgeGatewayRouteId.Value);
                }

                deployment.Installations.Add(installation);
            }

            dbContext.HomeLabDeployments.Add(deployment);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Success(
                "Home Lab deployment installed.",
                $"{deployment.Name} is installed on network {networkName}.",
                output,
                HomeLabHealthState.Starting);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Home Lab deployment {DeploymentId} failed.", deploymentId);
            foreach (var routeId in createdRoutes)
            {
                try { await edgeGatewayService.DeleteRouteAsync(routeId, cancellationToken); }
                catch (Exception cleanupException) { logger.LogDebug(cleanupException, "Could not remove Home Lab Edge Gateway route {RouteId}.", routeId); }
            }

            foreach (var containerName in createdContainers)
            {
                try { await RunDockerAsync(["rm", "--force", containerName], $"Clean up Home Lab container {containerName}", cancellationToken); }
                catch (Exception cleanupException) { logger.LogDebug(cleanupException, "Could not clean up Home Lab container {ContainerName}.", containerName); }
            }

            try { await RunDockerAsync(["network", "rm", networkName], $"Clean up Home Lab network {networkName}", cancellationToken); }
            catch (Exception cleanupException) { logger.LogDebug(cleanupException, "Could not clean up Home Lab network {NetworkName}.", networkName); }
            return Failure("Home Lab installation failed.", exception.Message, output, HomeLabHealthState.Failed);
        }
    }

    private HomeLabInstallationEntity BuildInstallationEntity(
        HomeLabDeploymentEntity deployment,
        HomeLabAppManifest app,
        IReadOnlyDictionary<string, string>? storagePaths,
        IReadOnlyDictionary<string, string>? configuration,
        bool isRecipe,
        DateTimeOffset now)
    {
        var bindings = app.Volumes.Select(volume => new HomeLabVolumeBinding(
            ResolveVolumeHostPath(deployment.Id, app, volume, storagePaths),
            volume.ContainerPath,
            volume.ReadOnly)).ToArray();
        var normalizedConfiguration = NormalizeConfiguration(app, configuration);
        return new HomeLabInstallationEntity
        {
            Id = Guid.NewGuid(),
            DeploymentId = deployment.Id,
            AppId = app.Id,
            DisplayName = app.Name,
            ContainerName = BuildContainerName(deployment.Id, app.Id),
            NetworkName = deployment.NetworkName,
            Image = $"{app.ImageRepository}:{app.ImageTag}",
            VolumeMappingsJson = JsonSerializer.Serialize(bindings, JsonOptions),
            ConfigurationJson = JsonSerializer.Serialize(normalizedConfiguration, JsonOptions),
            HealthState = (int)HomeLabHealthState.Starting,
            HealthDetail = "Container is starting.",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            IsRecipeInstallation = isRecipe
        };
    }

    private async Task<ContainerRunResult> RunContainerAsync(
        HomeLabInstallationEntity installation,
        HomeLabAppManifest app,
        IReadOnlyList<HomeLabVolumeBinding> bindings,
        IReadOnlyDictionary<string, string> configuration,
        List<string> output,
        CancellationToken cancellationToken)
    {
        foreach (var binding in bindings)
        {
            var mkdir = await RunAsync(
                new LinuxCommandRequest(
                    "mkdir",
                    ["-p", binding.HostPath],
                    true,
                    TimeSpan.FromSeconds(30),
                    $"Prepare Home Lab volume {binding.ContainerPath}"),
                cancellationToken);
            AppendOutput(output, mkdir);
            if (mkdir.ExitCode != 0)
            {
                return ContainerRunResult.Failed(Failure("Home Lab volume setup failed.", NormalizeFailure(mkdir), output, HomeLabHealthState.Failed));
            }
        }

        var args = new List<string>
        {
            "run", "--detach", "--name", installation.ContainerName,
            "--restart", "unless-stopped", "--network", installation.NetworkName,
            "--label", "com.linuxmadesane.homelab=true",
            "--label", $"com.linuxmadesane.homelab.app={app.Id}",
            "--label", $"com.linuxmadesane.homelab.deployment={installation.DeploymentId}"
        };
        foreach (var environment in app.Environment
                     .Concat(configuration)
                     .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.Last()))
        {
            args.AddRange(["--env", $"{environment.Key}={environment.Value}"]);
        }

        foreach (var binding in bindings)
        {
            args.AddRange(["--volume", $"{binding.HostPath}:{binding.ContainerPath}{(binding.ReadOnly ? ":ro" : string.Empty)}"]);
        }

        foreach (var port in app.Ports)
        {
            args.AddRange(["--publish", $"127.0.0.1::{port.ContainerPort}"]);
        }

        if (app.HealthCheck?.DockerCommand is { Length: > 0 } healthCommand)
        {
            args.AddRange([
                "--health-cmd", healthCommand,
                "--health-interval", $"{app.HealthCheck.IntervalSeconds}s",
                "--health-timeout", $"{app.HealthCheck.TimeoutSeconds}s",
                "--health-retries", app.HealthCheck.Retries.ToString(),
                "--health-start-period", $"{app.HealthCheck.StartPeriodSeconds}s"
            ]);
        }

        args.Add(installation.Image);
        var run = await RunDockerAsync(args, $"Install Home Lab app {app.Name}", cancellationToken);
        AppendOutput(output, run);
        if (run.ExitCode != 0)
        {
            return ContainerRunResult.Failed(Failure("Home Lab container creation failed.", NormalizeFailure(run), output, HomeLabHealthState.Failed));
        }

        var inspect = await InspectContainerAsync(installation.ContainerName, cancellationToken);
        if (inspect is null)
        {
            return ContainerRunResult.Failed(Failure("Home Lab container inspection failed.", "The container was created but LMS could not read its published ports.", output, HomeLabHealthState.Failed));
        }

        var ports = ParsePortBindings(inspect, app);
        if (ports.Count != app.Ports.Count)
        {
            return ContainerRunResult.Failed(Failure("Home Lab port inspection failed.", "The container was created but LMS could not resolve all published ports.", output, HomeLabHealthState.Failed));
        }

        return ContainerRunResult.Completed(ports);
    }

    private async Task<HomeLabOperationResult> EnsureNetworkAsync(string networkName, CancellationToken cancellationToken)
    {
        var inspect = await RunDockerAsync(["network", "inspect", networkName], $"Inspect Home Lab network {networkName}", cancellationToken);
        if (inspect.ExitCode == 0)
        {
            return Success("Home Lab network ready.", $"Reusing {networkName}.", [], HomeLabHealthState.Healthy);
        }

        var create = await RunDockerAsync(
            ["network", "create", "--driver", "bridge", "--label", "com.linuxmadesane.homelab=true", networkName],
            $"Create Home Lab network {networkName}",
            cancellationToken);
        return create.ExitCode == 0
            ? Success("Home Lab network created.", $"Created {networkName}.", [create.StandardOutput], HomeLabHealthState.Healthy)
            : Failure("Home Lab network setup failed.", NormalizeFailure(create), [create.StandardError], HomeLabHealthState.Failed);
    }

    private async Task RefreshHealthInternalAsync(HomeLabInstallationEntity installation, CancellationToken cancellationToken)
    {
        var inspect = await InspectContainerAsync(installation.ContainerName, cancellationToken);
        var health = inspect is null
            ? (HomeLabHealthState.Failed, "Container could not be inspected.")
            : ResolveHealth(inspect);
        installation.HealthState = (int)health.Item1;
        installation.HealthDetail = health.Item2;
        installation.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private async Task<JsonObject?> InspectContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync(
            ["inspect", "--format", "{{json .}}", containerName],
            $"Inspect Home Lab container {containerName}",
            cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        try { return JsonNode.Parse(result.StandardOutput.Trim())?.AsObject(); }
        catch (JsonException) { return null; }
    }

    private async Task<LinuxCommandResult> RunDockerAsync(
        IReadOnlyList<string> arguments,
        string description,
        CancellationToken cancellationToken) =>
        await RunAsync(
            new LinuxCommandRequest("docker", arguments, true, DockerCommandTimeout, description)
            {
                IsOptionalExternalTool = true
            },
            cancellationToken);

    private async Task<LinuxCommandResult> RunAsync(
        LinuxCommandRequest request,
        CancellationToken cancellationToken) =>
        await commandRunner.RunAsync(request, false, cancellationToken);

    private static IReadOnlyList<HomeLabPortBinding> ParsePortBindings(JsonObject inspect, HomeLabAppManifest app)
    {
        var ports = inspect["NetworkSettings"]?["Ports"]?.AsObject();
        if (ports is null)
        {
            return [];
        }

        var result = new List<HomeLabPortBinding>();
        foreach (var port in app.Ports)
        {
            var key = $"{port.ContainerPort}/{port.Protocol}";
            var published = ports[key]?.AsArray()?.FirstOrDefault()?.AsObject()?["HostPort"]?.GetValue<string>();
            if (!int.TryParse(published, out var hostPort))
            {
                continue;
            }
            result.Add(new HomeLabPortBinding(port.Name, port.ContainerPort, hostPort));
        }
        return result;
    }

    private static (HomeLabHealthState, string) ResolveHealth(JsonObject inspect)
    {
        var state = inspect["State"]?.AsObject();
        var status = state?["Status"]?.GetValue<string>() ?? string.Empty;
        var error = state?["Error"]?.GetValue<string>() ?? string.Empty;
        var healthStatus = state?["Health"]?["Status"]?.GetValue<string>() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(error)) return (HomeLabHealthState.Failed, error);
        if (status.Equals("running", StringComparison.OrdinalIgnoreCase) && healthStatus.Equals("unhealthy", StringComparison.OrdinalIgnoreCase)) return (HomeLabHealthState.Degraded, "Docker reports the container as unhealthy.");
        if (status.Equals("running", StringComparison.OrdinalIgnoreCase) && healthStatus.Equals("starting", StringComparison.OrdinalIgnoreCase)) return (HomeLabHealthState.Starting, "Docker health check is still starting.");
        if (status.Equals("running", StringComparison.OrdinalIgnoreCase)) return (HomeLabHealthState.Healthy, "Container is running.");
        if (status is "created" or "restarting") return (HomeLabHealthState.Starting, $"Docker container state: {status}.");
        if (status is "exited" or "dead") return (HomeLabHealthState.Stopped, $"Docker container state: {status}.");
        return (HomeLabHealthState.Failed, $"Docker container state: {status}.");
    }

    private static HomeLabPortManifest ResolvePrimaryPort(HomeLabAppManifest app) =>
        app.Ports.FirstOrDefault(port => port.Primary) ?? app.Ports.First();

    private string ResolveVolumeHostPath(
        Guid deploymentId,
        HomeLabAppManifest app,
        HomeLabVolumeManifest volume,
        IReadOnlyDictionary<string, string>? storagePaths)
    {
        if (!string.IsNullOrWhiteSpace(volume.SharedRole))
        {
            var supplied = storagePaths?.FirstOrDefault(item => item.Key.Equals(volume.SharedRole, StringComparison.OrdinalIgnoreCase)).Value;
            if (!string.IsNullOrWhiteSpace(supplied))
            {
                return NormalizeHostPath(supplied);
            }

            var stored = dbContext.HomeLabStorageRoles
                .AsNoTracking()
                .SingleOrDefault(item => item.Role == volume.SharedRole);
            if (!string.IsNullOrWhiteSpace(stored?.HostPath))
            {
                return NormalizeHostPath(stored.HostPath);
            }

            throw new InvalidOperationException($"Choose a host path for shared storage role '{volume.SharedRole}'.");
        }

        return Path.Combine(storageOptions.RootPath, "deployments", deploymentId.ToString("N"), app.Id, volume.Id);
    }

    private static Dictionary<string, string> NormalizeConfiguration(
        HomeLabAppManifest app,
        IReadOnlyDictionary<string, string>? configuration)
    {
        var result = new Dictionary<string, string>(app.Environment, StringComparer.OrdinalIgnoreCase);
        if (configuration is null)
        {
            return result;
        }

        foreach (var item in configuration)
        {
            var schema = app.ConfigurationSchema.FirstOrDefault(field => field.Id.Equals(item.Key, StringComparison.OrdinalIgnoreCase));
            if (schema is null)
            {
                throw new InvalidOperationException($"Configuration field '{item.Key}' is not supported by {app.Name}.");
            }
            if (schema.Secret)
            {
                throw new InvalidOperationException($"Secret configuration for {app.Name} is reserved for the LMS secret-backed VPN phase.");
            }
            result[schema.Id] = item.Value ?? string.Empty;
        }
        return result;
    }

    private static string NormalizeRole(string role)
    {
        var normalized = role?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new InvalidOperationException("Storage role names may contain only letters, numbers, hyphens, and underscores.");
        }
        return normalized;
    }

    private static string NormalizeHostPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Enter a host path for the storage role.");
        }
        var normalized = Path.GetFullPath(path.Trim());
        if (normalized == Path.GetPathRoot(normalized))
        {
            throw new InvalidOperationException("Use a child directory for Home Lab storage.");
        }
        return normalized;
    }

    private static string BuildContainerName(Guid deploymentId, string appId) =>
        $"lms-homelab-{appId}-{deploymentId.ToString("N")[..8]}";

    private static string BuildNetworkName(Guid deploymentId) =>
        $"lms-homelab-{deploymentId.ToString("N")[..12]}";

    private static bool ContainsNoSuchContainer(LinuxCommandResult result) =>
        result.StandardError.Contains("No such container", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeFailure(LinuxCommandResult result) =>
        FirstNonEmpty(result.StandardError, result.StandardOutput, $"Docker exited with code {result.ExitCode}.");

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "The Docker command failed.";

    private static void EnsureSuccess(LinuxCommandResult result, string message)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{message} {NormalizeFailure(result)}");
        }
    }

    private static void AppendOutput(List<string> output, LinuxCommandResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StandardOutput)) output.Add(result.StandardOutput.Trim());
        if (!string.IsNullOrWhiteSpace(result.StandardError)) output.Add(result.StandardError.Trim());
    }

    private static HomeLabOperationResult Success(
        string summary,
        string detail,
        IReadOnlyList<string> output,
        HomeLabHealthState state) =>
        new(true, summary, detail, output, state, DateTimeOffset.UtcNow);

    private static HomeLabOperationResult Failure(
        string summary,
        string detail,
        IReadOnlyList<string> output,
        HomeLabHealthState state) =>
        new(false, summary, detail, output, state, DateTimeOffset.UtcNow);

    private static HomeLabHealthState ToHealth(int state) =>
        Enum.IsDefined(typeof(HomeLabHealthState), state)
            ? (HomeLabHealthState)state
            : HomeLabHealthState.Failed;

    private static HomeLabDeployment MapDeployment(HomeLabDeploymentEntity item) =>
        new(item.Id, item.Name, item.RecipeId, item.NetworkName, item.CreatedAtUtc, item.UpdatedAtUtc);

    private static HomeLabStorageRole MapStorageRole(HomeLabStorageRoleEntity item) =>
        new(item.Role, item.HostPath, item.UpdatedAtUtc);

    private static HomeLabAppInstallation MapInstallation(HomeLabInstallationEntity item) =>
        new(
            item.Id,
            item.DeploymentId,
            item.AppId,
            item.DisplayName,
            item.ContainerName,
            item.NetworkName,
            item.Image,
            item.VolumeMappingsJson,
            item.PortMappingsJson,
            item.EdgeGatewayRouteId,
            ToHealth(item.HealthState),
            item.HealthDetail,
            item.CreatedAtUtc,
            item.UpdatedAtUtc,
            item.IsRecipeInstallation);

    private static Dictionary<string, string> DeserializeDictionary(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
        ?? new(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<HomeLabVolumeBinding> DeserializeBindings(string json) =>
        JsonSerializer.Deserialize<List<HomeLabVolumeBinding>>(json, JsonOptions) ?? [];

    private static IReadOnlyList<HomeLabPortBinding> DeserializePortBindings(string json) =>
        JsonSerializer.Deserialize<List<HomeLabPortBinding>>(json, JsonOptions) ?? [];

    private sealed record HomeLabVolumeBinding(string HostPath, string ContainerPath, bool ReadOnly);
    private sealed record HomeLabPortBinding(string Name, int ContainerPort, int HostPort);

    private sealed record ContainerRunResult(
        bool Succeeded,
        IReadOnlyList<HomeLabPortBinding> PortBindings,
        HomeLabOperationResult? Result)
    {
        public static ContainerRunResult Completed(IReadOnlyList<HomeLabPortBinding> ports) =>
            new(true, ports, null);

        public static ContainerRunResult Failed(HomeLabOperationResult result) =>
            new(false, [], result);
    }
}
