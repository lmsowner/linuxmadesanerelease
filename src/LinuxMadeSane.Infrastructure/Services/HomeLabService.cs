// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;
using LinuxMadeSane.Application.Contracts.Caddy;
using LinuxMadeSane.Application.Contracts.EdgeGateway;
using LinuxMadeSane.Application.Contracts.HomeLab;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Caddy;
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
    ICaddyIntegrationService caddyIntegrationService,
    ISecretStore secretStore,
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
            try
            {
                var app = HomeLabCatalog.GetApp(entity.AppId);
                await EnforceRequiredVpnRouteAsync(entity, app, cancellationToken);
                var applicationConfigurationChanged = await EnsureApplicationConfigurationAsync(
                    app,
                    DeserializeBindings(entity.VolumeMappingsJson),
                    cancellationToken);
                if (applicationConfigurationChanged && entity.HealthState != (int)HomeLabHealthState.Stopped)
                {
                    var restart = await RunDockerAsync(
                        ["restart", entity.ContainerName],
                        $"Restart Home Lab app {entity.DisplayName} after applying proxy compatibility settings",
                        cancellationToken);
                    if (restart.ExitCode != 0)
                    {
                        logger.LogWarning(
                            "Could not restart Home Lab app {AppId} after applying compatibility settings: {Error}",
                            entity.AppId,
                            NormalizeFailure(restart));
                    }
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not apply application compatibility settings for Home Lab app {AppId}.", entity.AppId);
            }

            await RefreshHealthInternalAsync(entity, cancellationToken);
            try
            {
                await EnsureCaddyAccessAsync(entity, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not prepare the LMS Caddy access route for Home Lab app {AppId}.", entity.AppId);
            }
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

        return new HomeLabWorkspace(HomeLabCatalog.VisibleApps, HomeLabCatalog.Recipes, storageRoles, deployments, installations);
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
            request.SecretConfiguration,
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
            request.SecretConfiguration,
            null,
            request.RecipeId,
            request.AppIds,
            cancellationToken);
    }

    public async Task<HomeLabOperationResult> SetNetworkRouteAsync(
        Guid installationId,
        bool useVpnGateway,
        Guid? gatewayInstallationId = null,
        CancellationToken cancellationToken = default)
    {
        var installation = await dbContext.HomeLabInstallations
            .SingleOrDefaultAsync(item => item.Id == installationId, cancellationToken)
            ?? throw new InvalidOperationException("The Home Lab installation was not found.");
        var app = HomeLabCatalog.GetApp(installation.AppId);
        if (!app.SupportsVpnGateway)
        {
            return Failure(
                "Network route cannot be changed.",
                $"{app.Name} does not support VPN Gateway routing.",
                [],
                ToHealth(installation.HealthState));
        }

        if (installation.IsRecipeInstallation)
        {
            return Failure(
                "Network route is managed by the recipe.",
                $"Remove and reinstall the recipe to change how {app.Name} is routed.",
                [],
                ToHealth(installation.HealthState));
        }

        if (app.RequiresVpnGateway && !useVpnGateway)
        {
            return Failure(
                "Direct route is blocked.",
                $"{app.Name} must remain behind VPN Gateway (Gluetun).",
                [],
                HomeLabHealthState.Blocked);
        }

        var newNetworkMode = "bridge";
        HomeLabInstallationEntity? selectedGateway = null;
        if (useVpnGateway)
        {
            selectedGateway = await ResolveVpnGatewayAsync(gatewayInstallationId, cancellationToken);
            if (selectedGateway is null)
            {
                return Failure(
                    "VPN route was not applied.",
                    "Install and configure the VPN Gateway first. LMS will not fall back to a direct route.",
                    [],
                    HomeLabHealthState.Blocked);
            }

            await EnsureVpnNamespacePortAvailabilityAsync(app, selectedGateway.ContainerName, installation.Id, cancellationToken);
            newNetworkMode = $"container:{selectedGateway.ContainerName}";
        }

        if (installation.NetworkMode.Equals(newNetworkMode, StringComparison.OrdinalIgnoreCase))
        {
            PersistNetworkRouteConfiguration(installation, useVpnGateway, selectedGateway?.Id);
            await RefreshHealthInternalAsync(installation, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Success(
                "Network route already configured.",
                useVpnGateway ? "This app is already routed through the VPN Gateway (Gluetun)." : "This app is already using a direct route without VPN.",
                [],
                ToHealth(installation.HealthState));
        }

        var output = new List<string>();
        var oldNetworkMode = installation.NetworkMode;
        var oldConfigurationJson = installation.ConfigurationJson;
        var remove = await RunDockerAsync(
            ["rm", "--force", installation.ContainerName],
            $"Recreate {app.Name} with the selected network route",
            cancellationToken);
        AppendOutput(output, remove);
        if (remove.ExitCode != 0 && !ContainsNoSuchContainer(remove))
        {
            return Failure("Network route change failed.", NormalizeFailure(remove), output, HomeLabHealthState.Failed);
        }

        installation.NetworkMode = newNetworkMode;
        PersistNetworkRouteConfiguration(installation, useVpnGateway, selectedGateway?.Id);
        var run = await RunContainerAsync(
            installation,
            app,
            DeserializeBindings(installation.VolumeMappingsJson),
            DeserializeDictionary(installation.ConfigurationJson),
            DeserializeDictionary(installation.SecretConfigurationJson),
            output,
            cancellationToken);
        if (!run.Succeeded)
        {
            installation.NetworkMode = oldNetworkMode;
            installation.ConfigurationJson = oldConfigurationJson;
            var rollback = await RunContainerAsync(
                installation,
                app,
                DeserializeBindings(installation.VolumeMappingsJson),
                DeserializeDictionary(installation.ConfigurationJson),
                DeserializeDictionary(installation.SecretConfigurationJson),
                output,
                cancellationToken);
            var rollbackDetail = rollback.Succeeded
                ? " The previous route was restored."
                : " LMS could not restore the previous route; use the container logs and Settings to recover it.";
            return Failure(
                "Network route change failed.",
                $"{run.Result?.Detail ?? "The container could not be recreated."}{rollbackDetail}",
                output,
                HomeLabHealthState.Failed);
        }

        installation.PortMappingsJson = JsonSerializer.Serialize(run.PortBindings, JsonOptions);
        await UpdateCaddyAccessAsync(installation, app, run.PortBindings, cancellationToken);
        installation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await RefreshHealthInternalAsync(installation, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Success(
            "Network route updated.",
            useVpnGateway
                ? $"{app.Name} now uses VPN Gateway (Gluetun). It will remain blocked if the gateway is unavailable."
                : $"{app.Name} now uses a direct route without VPN.",
            output,
            ToHealth(installation.HealthState));
    }

    public async Task<HomeLabNetworkSecurity> GetNetworkSecurityAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var installation = await dbContext.HomeLabInstallations
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == installationId, cancellationToken)
            ?? throw new InvalidOperationException("The Home Lab installation was not found.");
        var app = HomeLabCatalog.GetApp(installation.AppId);
        var checkedAtUtc = DateTimeOffset.UtcNow;
        var isVpnRouted = installation.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase);
        var gatewayContainer = isVpnRouted
            ? installation.NetworkMode["container:".Length..]
            : string.Empty;

        if (!isVpnRouted)
        {
            var publicIp = await ResolvePublicIpAsync(installation.ContainerName, cancellationToken);
            var detail = app.RequiresVpnGateway
                ? $"{app.Name} is on a direct Docker network. It is not protected by Gluetun and has been blocked."
                : $"{app.Name} is using a direct Docker network. No VPN gateway is active.";
            return new HomeLabNetworkSecurity(
                app.RequiresVpnGateway ? "UNSAFE" : "DIRECT",
                false,
                false,
                "Direct (NO VPN)",
                string.Empty,
                "Not used",
                publicIp,
                detail,
                checkedAtUtc);
        }

        var gatewayInspect = await InspectContainerAsync(gatewayContainer, cancellationToken);
        if (gatewayInspect is null)
        {
            return new HomeLabNetworkSecurity(
                "BLOCKED",
                true,
                false,
                $"VPN Gateway (Gluetun): {gatewayContainer}",
                gatewayContainer,
                "Unavailable",
                null,
                "The configured Gluetun gateway container cannot be inspected. Internet access is blocked by the dependency state.",
                checkedAtUtc);
        }

        var gatewayHealth = ResolveHealth(gatewayInspect);
        var appInspect = await InspectContainerAsync(installation.ContainerName, cancellationToken);
        var actualNetworkMode = appInspect?["HostConfig"]?["NetworkMode"]?.GetValue<string>();
        var expectedNetworkMode = $"container:{gatewayContainer}";
        if (!string.Equals(actualNetworkMode, expectedNetworkMode, StringComparison.OrdinalIgnoreCase))
        {
            return new HomeLabNetworkSecurity(
                "UNVERIFIED",
                true,
                false,
                $"VPN Gateway (Gluetun): {gatewayContainer}",
                gatewayContainer,
                gatewayHealth.Item1.ToString(),
                null,
                $"LMS expects Docker network mode '{expectedNetworkMode}', but the running container reports '{actualNetworkMode ?? "unknown"}'.",
                checkedAtUtc);
        }

        if (gatewayHealth.Item1 is not HomeLabHealthState.Healthy)
        {
            return new HomeLabNetworkSecurity(
                "BLOCKED",
                true,
                false,
                $"VPN Gateway (Gluetun): {gatewayContainer}",
                gatewayContainer,
                gatewayHealth.Item1.ToString(),
                null,
                $"Gluetun is {gatewayHealth.Item1}. LMS will not fall back to a direct route.",
                checkedAtUtc);
        }

        var publicIpFromGateway = await ResolvePublicIpAsync(gatewayContainer, cancellationToken);
        if (string.IsNullOrWhiteSpace(publicIpFromGateway))
        {
            return new HomeLabNetworkSecurity(
                "UNVERIFIED",
                true,
                false,
                $"VPN Gateway (Gluetun): {gatewayContainer}",
                gatewayContainer,
                gatewayHealth.Item1.ToString(),
                null,
                "Docker confirms the app shares Gluetun's network namespace, but LMS could not retrieve the gateway's public IP.",
                checkedAtUtc);
        }

        return new HomeLabNetworkSecurity(
            "SECURED",
            true,
            true,
            $"VPN Gateway (Gluetun): {gatewayContainer}",
            gatewayContainer,
            gatewayHealth.Item1.ToString(),
            publicIpFromGateway,
            "Docker network mode, Gluetun health, and the gateway egress IP were verified.",
            checkedAtUtc);
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
        if (app.RequiresVpnGateway &&
            !installation.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase) &&
            action != HomeLabLifecycleAction.Remove &&
            action != HomeLabLifecycleAction.RefreshHealth)
        {
            return Failure(
                "App is blocked for safety.",
                $"{app.Name} requires VPN Gateway (Gluetun) routing and cannot be started or updated on a direct network.",
                [],
                HomeLabHealthState.Blocked);
        }
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
            var caddyRouteId = installation.CaddyRouteId;
            var secretReferences = DeserializeDictionary(installation.SecretConfigurationJson).Values.ToArray();
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
            if (caddyRouteId.HasValue)
            {
                await caddyIntegrationService.DeleteRouteAsync(caddyRouteId.Value, cancellationToken);
            }
            foreach (var secretReference in secretReferences)
            {
                await secretStore.DeleteSecretAsync(secretReference, cancellationToken);
            }

            return Success(
                "Home Lab app removed.",
                $"{app.Name} was removed. Persistent host data was left in place.",
                output,
                HomeLabHealthState.Stopped);
        }

        if (action == HomeLabLifecycleAction.Update)
        {
            if (app.Id.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase))
            {
                return await UpdateVpnGatewayAsync(installation, output, cancellationToken);
            }

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
                DeserializeDictionary(installation.SecretConfigurationJson),
                output,
                cancellationToken);
            if (!run.Succeeded)
            {
                return run.Result!;
            }

            installation.PortMappingsJson = JsonSerializer.Serialize(run.PortBindings, JsonOptions);
            await UpdateCaddyAccessAsync(installation, app, run.PortBindings, cancellationToken);
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

    private async Task<HomeLabOperationResult> UpdateVpnGatewayAsync(
        HomeLabInstallationEntity gateway,
        List<string> output,
        CancellationToken cancellationToken)
    {
        var routedInstallations = await dbContext.HomeLabInstallations
            .Where(item => item.NetworkMode == $"container:{gateway.ContainerName}")
            .ToListAsync(cancellationToken);

        var pull = await RunDockerAsync(
            ["pull", gateway.Image],
            $"Pull updated Home Lab VPN Gateway image {gateway.Image}",
            cancellationToken);
        AppendOutput(output, pull);
        if (pull.ExitCode != 0)
        {
            return Failure("VPN Gateway update failed.", NormalizeFailure(pull), output, HomeLabHealthState.Failed);
        }

        foreach (var routed in routedInstallations)
        {
            var removeRouted = await RunDockerAsync(
                ["rm", "--force", routed.ContainerName],
                $"Pause routed Home Lab app {routed.ContainerName} for VPN Gateway update",
                cancellationToken);
            AppendOutput(output, removeRouted);
            if (removeRouted.ExitCode != 0 && !ContainsNoSuchContainer(removeRouted))
            {
                MarkVpnDependentsBlocked(routedInstallations, "VPN Gateway update could not pause all routed apps.");
                await dbContext.SaveChangesAsync(cancellationToken);
                return Failure("VPN Gateway update could not pause routed apps.", NormalizeFailure(removeRouted), output, HomeLabHealthState.Failed);
            }
        }

        var removeGateway = await RunDockerAsync(
            ["rm", "--force", gateway.ContainerName],
            $"Replace Home Lab VPN Gateway container {gateway.ContainerName}",
            cancellationToken);
        AppendOutput(output, removeGateway);
        if (removeGateway.ExitCode != 0 && !ContainsNoSuchContainer(removeGateway))
        {
            MarkVpnDependentsBlocked(routedInstallations, "VPN Gateway update could not replace the gateway container.");
            await dbContext.SaveChangesAsync(cancellationToken);
            return Failure("VPN Gateway container replacement failed.", NormalizeFailure(removeGateway), output, HomeLabHealthState.Failed);
        }

        var gatewayRun = await RunContainerAsync(
            gateway,
            HomeLabCatalog.GetApp(gateway.AppId),
            DeserializeBindings(gateway.VolumeMappingsJson),
            DeserializeDictionary(gateway.ConfigurationJson),
            DeserializeDictionary(gateway.SecretConfigurationJson),
            output,
            cancellationToken);
        if (!gatewayRun.Succeeded)
        {
            gateway.HealthState = (int)HomeLabHealthState.Failed;
            gateway.HealthDetail = "VPN Gateway replacement failed; routed apps remain blocked.";
            MarkVpnDependentsBlocked(routedInstallations, gateway.HealthDetail);
            await dbContext.SaveChangesAsync(cancellationToken);
            return gatewayRun.Result!;
        }

        gateway.PortMappingsJson = JsonSerializer.Serialize(gatewayRun.PortBindings, JsonOptions);
        gateway.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await RefreshHealthInternalAsync(gateway, cancellationToken);

        var failedRoutedApps = new List<string>();
        foreach (var routed in routedInstallations)
        {
            var app = HomeLabCatalog.GetApp(routed.AppId);
            var routedRun = await RunContainerAsync(
                routed,
                app,
                DeserializeBindings(routed.VolumeMappingsJson),
                DeserializeDictionary(routed.ConfigurationJson),
                DeserializeDictionary(routed.SecretConfigurationJson),
                output,
                cancellationToken);
            if (!routedRun.Succeeded)
            {
                routed.HealthState = (int)HomeLabHealthState.Blocked;
                routed.HealthDetail = "Blocked: VPN Gateway was updated, but this app could not be recreated.";
                failedRoutedApps.Add(app.Name);
                continue;
            }

            routed.PortMappingsJson = JsonSerializer.Serialize(routedRun.PortBindings, JsonOptions);
            await UpdateCaddyAccessAsync(routed, app, routedRun.PortBindings, cancellationToken);
            routed.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await RefreshHealthInternalAsync(routed, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        if (failedRoutedApps.Count > 0)
        {
            return Failure(
                "VPN Gateway updated with blocked routed apps.",
                $"The gateway was updated, but these apps need attention: {string.Join(", ", failedRoutedApps)}.",
                output,
                HomeLabHealthState.Degraded);
        }

        return Success(
            "VPN Gateway updated.",
            routedInstallations.Count == 0
                ? $"{gateway.DisplayName} was recreated from {gateway.Image}."
                : $"{gateway.DisplayName} was recreated and {routedInstallations.Count} routed app(s) were reconnected.",
            output,
            ToHealth(gateway.HealthState));
    }

    private static void MarkVpnDependentsBlocked(
        IEnumerable<HomeLabInstallationEntity> installations,
        string detail)
    {
        foreach (var installation in installations)
        {
            installation.HealthState = (int)HomeLabHealthState.Blocked;
            installation.HealthDetail = $"Blocked: {detail}";
            installation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
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
        var logs = string.Join(
            Environment.NewLine,
            new[] { result.StandardOutput, result.StandardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new HomeLabLogsResult(
            result.ExitCode == 0,
            logs,
            result.ExitCode == 0 ? string.Empty : NormalizeFailure(result));
    }

    public async Task<HomeLabOperationResult> ResetCredentialsAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var installation = await dbContext.HomeLabInstallations
            .SingleOrDefaultAsync(item => item.Id == installationId, cancellationToken)
            ?? throw new InvalidOperationException("The Home Lab installation was not found.");
        var app = HomeLabCatalog.GetApp(installation.AppId);
        if (!app.Id.Equals("qbittorrent", StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                "Credential reset is not available.",
                $"{app.Name} does not expose an LMS credential reset integration.",
                [],
                ToHealth(installation.HealthState));
        }

        var output = new List<string>();
        var stop = await RunDockerAsync(
            ["stop", installation.ContainerName],
            $"Stop {app.Name} before resetting credentials",
            cancellationToken);
        AppendOutput(output, stop);
        if (stop.ExitCode != 0 && !ContainsNoSuchContainer(stop))
        {
            return Failure("Credential reset failed.", NormalizeFailure(stop), output, HomeLabHealthState.Failed);
        }

        var reset = await ResetQbittorrentCredentialsAsync(
            DeserializeBindings(installation.VolumeMappingsJson),
            cancellationToken);
        AppendOutput(output, reset);

        var start = await RunDockerAsync(
            ["start", installation.ContainerName],
            $"Start {app.Name} after resetting credentials",
            cancellationToken);
        AppendOutput(output, start);
        if (reset.ExitCode != 0)
        {
            await RefreshHealthInternalAsync(installation, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Failure("Credential reset failed.", NormalizeFailure(reset), output, HomeLabHealthState.Failed);
        }
        if (start.ExitCode != 0)
        {
            installation.HealthState = (int)HomeLabHealthState.Failed;
            installation.HealthDetail = NormalizeFailure(start);
            installation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            return Failure("Credential reset restart failed.", installation.HealthDetail, output, HomeLabHealthState.Failed);
        }

        await RefreshHealthInternalAsync(installation, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Success(
            "qBittorrent credentials reset.",
            "Username: admin. qBittorrent generated a new temporary password; open the container logs below to copy it, then change it in qBittorrent settings.",
            output,
            ToHealth(installation.HealthState));
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

        var edgeGatewayRoutes = await Task.WhenAll(
            installations
                .Where(item => item.EdgeGatewayRouteId.HasValue)
                .Select(async item =>
                {
                    var editor = await edgeGatewayService.GetEditorAsync(item.EdgeGatewayRouteId, cancellationToken);
                    return (item.Id, Editor: editor);
                }));
        var edgeGatewayRoutesByInstallation = edgeGatewayRoutes
            .ToDictionary(item => item.Id, item => item.Editor);

        return new HomeLabEffectiveConfiguration(
            deployment.Id,
            deployment.NetworkName,
            installations.Select(item => BuildEffectiveContainer(
                item,
                installations,
                deployment.RecipeId,
                edgeGatewayRoutesByInstallation.GetValueOrDefault(item.Id))).ToArray());
    }

    private static HomeLabEffectiveContainer BuildEffectiveContainer(
        HomeLabInstallationEntity installation,
        IReadOnlyList<HomeLabInstallationEntity> installations,
        string? recipeId,
        EdgeGatewayRouteEditor? edgeGatewayRoute)
    {
        var app = HomeLabCatalog.GetApp(installation.AppId);
        var bindings = DeserializeBindings(installation.VolumeMappingsJson);
        var ports = DeserializePortBindings(installation.PortMappingsJson);
        var configuration = DeserializeDictionary(installation.ConfigurationJson);
        var secretConfiguration = DeserializeDictionary(installation.SecretConfigurationJson);
        var environment = app.Environment
            .Concat(configuration.Where(item => !IsInternalConfigurationKey(item.Key)))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}={group.Last().Value}")
            .ToList();

        foreach (var secretKey in secretConfiguration.Keys)
        {
            environment.Add(secretKey.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase)
                ? $"File {secretKey["FILE:".Length..]}=<redacted secret file>"
                : $"{secretKey}=<redacted secret>");
        }

        var access = new List<string>();
        foreach (var port in ports)
        {
            var manifest = app.Ports.FirstOrDefault(candidate => candidate.Name.Equals(port.Name, StringComparison.OrdinalIgnoreCase));
            var isWeb = manifest?.Name.Equals("web", StringComparison.OrdinalIgnoreCase) == true;
            access.Add(isWeb
                ? $"Docker UI: http://{app.Id}:{port.ContainerPort}"
                : $"Docker port: {app.Id}:{port.ContainerPort}/{manifest?.Protocol ?? "tcp"}");
            if (port.HostPort > 0)
            {
                access.Add(isWeb
                    ? installation.CaddySourcePort is int caddyPort
                        ? $"LMS Caddy UI: http://<LMS host>:{caddyPort}"
                        : $"Server-only UI: http://127.0.0.1:{port.HostPort}"
                    : $"Host port: 127.0.0.1:{port.HostPort}");
            }
        }

        if (edgeGatewayRoute?.Id is not null &&
            !string.IsNullOrWhiteSpace(edgeGatewayRoute.Hostname) &&
            !string.IsNullOrWhiteSpace(edgeGatewayRoute.DomainName))
        {
            var hostname = edgeGatewayRoute.Hostname.Trim().TrimEnd('.');
            var domainName = edgeGatewayRoute.DomainName.Trim().TrimEnd('.');
            var publicHostname = hostname.EndsWith(domainName, StringComparison.OrdinalIgnoreCase)
                ? hostname
                : $"{hostname}.{domainName}";
            var path = edgeGatewayRoute.TargetPathPrefix.Trim();
            access.Add($"Edge Gateway: https://{publicHostname}{(string.IsNullOrWhiteSpace(path) ? string.Empty : path.StartsWith('/') ? path : $"/{path}")}");
            access.Add($"Edge Gateway auth: {edgeGatewayRoute.AuthMode}");
        }

        var usesVpnGateway = installation.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase);
        var connections = new List<string>
        {
            usesVpnGateway
                ? "INTERNET ROUTE: VPN Gateway (Gluetun)"
                : app.SupportsVpnGateway
                    ? "INTERNET ROUTE: Direct (NO VPN)"
                    : "Internet route: Direct",
            usesVpnGateway
                ? $"VPN gateway container: {installation.NetworkMode["container:".Length..]}"
                : $"Docker network: {installation.NetworkName}",
            usesVpnGateway
                ? $"Docker DNS name: {app.Id} is provided by the VPN gateway network namespace"
                : $"Docker DNS name: {app.Id}"
        };

        if (app.Id.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase))
        {
            var routedApps = installations
                .Where(item => item.NetworkMode.Equals($"container:{installation.ContainerName}", StringComparison.OrdinalIgnoreCase))
                .Select(item => HomeLabCatalog.GetApp(item.AppId).Name)
                .ToArray();
            if (routedApps.Length > 0)
            {
                connections.Add($"Routed apps: {string.Join(", ", routedApps)}");
            }
        }

        if (app.Id.Equals("qbittorrent", StringComparison.OrdinalIgnoreCase))
        {
            environment.Add("WebUI\\ReverseProxySupportEnabled=true");
            environment.Add("WebUI\\HostHeaderValidation=false");
            connections.Add("Web UI proxy support: enabled for the LMS Caddy route");
            connections.Add("Application login: use qBittorrent credentials; the initial admin password is in the container logs until changed");
        }

        if (recipeId is not null)
        {
            var relationship = HomeLabCatalog.GetRecipe(recipeId).Relationships
                .FirstOrDefault(item => item.AppId.Equals(app.Id, StringComparison.OrdinalIgnoreCase));
            if (relationship is not null)
            {
                AddConnection("VPN route", relationship.RouteVia);
                AddConnection("Download client", relationship.DownloadClient);
                AddConnection("Indexer manager", relationship.IndexerManager);
                AddConnection("Sonarr", relationship.Sonarr);
                AddConnection("Radarr", relationship.Radarr);
            }
        }

        void AddConnection(string label, string? appId)
        {
            if (!string.IsNullOrWhiteSpace(appId))
            {
                connections.Add($"{label}: {HomeLabCatalog.GetApp(appId).Name} ({appId})");
            }
        }

        return new HomeLabEffectiveContainer(
            installation.AppId,
            installation.ContainerName,
            installation.Image,
            installation.NetworkMode,
            bindings.Select(binding => $"{binding.HostPath}:{binding.ContainerPath}{(binding.ReadOnly ? ":ro" : string.Empty)}").ToArray(),
            ports.Select(binding => $"127.0.0.1:{binding.HostPort}:{binding.ContainerPort}").ToArray(),
            access,
            environment,
            connections,
            app.Dependencies);
    }

    private async Task EnsureCaddyAccessAsync(
        HomeLabInstallationEntity installation,
        CancellationToken cancellationToken)
    {
        var app = HomeLabCatalog.GetApp(installation.AppId);
        var primaryManifestPort = ResolvePrimaryPort(app);
        if (!primaryManifestPort.Name.Equals("web", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var ports = DeserializePortBindings(installation.PortMappingsJson);
        var primaryPort = ports.FirstOrDefault(port =>
            port.Name.Equals(primaryManifestPort.Name, StringComparison.OrdinalIgnoreCase));
        if (primaryPort is null || primaryPort.HostPort <= 0)
        {
            return;
        }

        var routeName = BuildCaddyRouteName(installation, app);
        if (installation.CaddyRouteId is Guid routeId)
        {
            var existing = await caddyIntegrationService.GetEditorAsync(routeId, cancellationToken);
            if (existing.Id == routeId)
            {
                installation.CaddySourcePort = existing.SourcePort;
                return;
            }

            installation.CaddyRouteId = null;
            installation.CaddySourcePort = null;
        }

        var dashboard = await caddyIntegrationService.GetDashboardAsync(cancellationToken);
        var savedRoute = dashboard.Routes.FirstOrDefault(route =>
            route.Kind == CaddyProxyRouteKind.PortForward &&
            route.Name.Equals(routeName, StringComparison.OrdinalIgnoreCase));
        if (savedRoute is not null)
        {
            var existing = await caddyIntegrationService.GetEditorAsync(savedRoute.Id, cancellationToken);
            if (existing.Id == savedRoute.Id)
            {
                existing.DestinationIp = "127.0.0.1";
                existing.DestinationPort = primaryPort.HostPort;
                installation.CaddyRouteId = savedRoute.Id;
                await caddyIntegrationService.SaveRouteAsync(existing, cancellationToken);
                installation.CaddySourcePort = existing.SourcePort;
                return;
            }
        }

        var sourcePort = await ResolveCaddySourcePortAsync(cancellationToken);
        var editor = new CaddyProxyRouteEditor
        {
            Kind = CaddyProxyRouteKind.PortForward,
            Name = routeName,
            Description = $"Home Lab browser access for {app.Name}. Managed by LMS.",
            SourceIp = "0.0.0.0",
            SourcePort = sourcePort,
            DestinationIp = "127.0.0.1",
            DestinationPort = primaryPort.HostPort,
            DestinationScheme = CaddyProxyTargetScheme.Http
        };
        installation.CaddyRouteId = await caddyIntegrationService.SaveRouteAsync(editor, cancellationToken);
        installation.CaddySourcePort = sourcePort;
    }

    private async Task UpdateCaddyAccessAsync(
        HomeLabInstallationEntity installation,
        HomeLabAppManifest app,
        IReadOnlyList<HomeLabPortBinding> ports,
        CancellationToken cancellationToken)
    {
        var primaryPort = ports.FirstOrDefault(port =>
            port.Name.Equals(ResolvePrimaryPort(app).Name, StringComparison.OrdinalIgnoreCase));
        if (primaryPort is null || primaryPort.HostPort <= 0)
        {
            return;
        }

        if (installation.CaddyRouteId is not Guid routeId)
        {
            await EnsureCaddyAccessAsync(installation, cancellationToken);
            return;
        }

        var editor = await caddyIntegrationService.GetEditorAsync(routeId, cancellationToken);
        if (editor.Id != routeId)
        {
            installation.CaddyRouteId = null;
            installation.CaddySourcePort = null;
            await EnsureCaddyAccessAsync(installation, cancellationToken);
            return;
        }

        editor.DestinationIp = "127.0.0.1";
        editor.DestinationPort = primaryPort.HostPort;
        await caddyIntegrationService.SaveRouteAsync(editor, cancellationToken);
        installation.CaddySourcePort = editor.SourcePort;
    }

    private async Task<int> ResolveCaddySourcePortAsync(CancellationToken cancellationToken)
    {
        var dashboard = await caddyIntegrationService.GetDashboardAsync(cancellationToken);
        var portForwardEditors = await Task.WhenAll(dashboard.Routes
            .Where(route => route.Kind == CaddyProxyRouteKind.PortForward)
            .Select(route => caddyIntegrationService.GetEditorAsync(route.Id, cancellationToken)));
        var usedPorts = portForwardEditors
            .Select(route => route.SourcePort)
            .ToHashSet();

        for (var port = 39000; port <= 39999; port++)
        {
            if (!usedPorts.Contains(port) && IsTcpPortAvailable(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException("LMS could not find an available Caddy access port for the Home Lab app.");
    }

    private static bool IsTcpPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string BuildCaddyRouteName(HomeLabInstallationEntity installation, HomeLabAppManifest app) =>
        $"Home Lab: {app.Name} ({installation.DeploymentId.ToString("N")[..8]})";

    private async Task<HomeLabOperationResult> InstallDeploymentAsync(
        string id,
        string? displayName,
        IReadOnlyDictionary<string, string>? storagePaths,
        IReadOnlyDictionary<string, string>? configuration,
        IReadOnlyDictionary<string, string>? secretConfiguration,
        HomeLabEdgeGatewayRequest? edgeGateway,
        string? recipeId,
        IReadOnlySet<string>? selectedAppIds,
        CancellationToken cancellationToken)
    {
        var standaloneApp = recipeId is null ? HomeLabCatalog.GetApp(id) : null;
        var standaloneUsesVpn = recipeId is null && IsVpnRouteSelected(configuration);
        if (standaloneApp?.RequiresVpnGateway == true && !standaloneUsesVpn)
        {
            throw new InvalidOperationException($"{standaloneApp.Name} requires VPN Gateway (Gluetun) routing. Direct installation is blocked for safety.");
        }
        if (standaloneUsesVpn && standaloneApp is not null && !standaloneApp.SupportsVpnGateway)
        {
            throw new InvalidOperationException($"{standaloneApp.Name} does not support VPN Gateway routing.");
        }

        var reusableVpnGateway = standaloneUsesVpn
            ? await ResolveVpnGatewayAsync(
                ResolveVpnGatewayId(configuration),
                cancellationToken)
            : null;
        if (standaloneUsesVpn && reusableVpnGateway is null)
        {
            throw new InvalidOperationException("Install and configure the VPN Gateway first, then choose 'VPN Gateway (Gluetun)' for this app.");
        }
        if (standaloneUsesVpn && reusableVpnGateway is not null)
        {
            await EnsureVpnNamespacePortAvailabilityAsync(
                standaloneApp!,
                reusableVpnGateway.ContainerName,
                null,
                cancellationToken);
        }

        IReadOnlyList<string> requestedAppIds = recipeId is null
            ? [id]
            : HomeLabCatalog.GetRecipe(recipeId).AppIds
                .Where(appId => selectedAppIds is null || selectedAppIds.Contains(appId, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        if (requestedAppIds.Count == 0)
        {
            throw new InvalidOperationException("Choose at least one Home Lab app.");
        }

        if (recipeId is not null && !HomeLabCatalog.GetRecipe(recipeId).IsInstallable)
        {
            throw new InvalidOperationException("This recipe is catalogued for a later Home Lab phase and is not installable yet.");
        }

        var apps = ExpandDependencies(requestedAppIds).Select(HomeLabCatalog.GetApp).ToArray();
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
        var createdCaddyRoutes = new List<Guid>();
        var createdSecretReferences = new List<string>();
        var relationships = recipeId is null
            ? new Dictionary<string, HomeLabRecipeRelationship>(StringComparer.OrdinalIgnoreCase)
            : HomeLabCatalog.GetRecipe(recipeId).Relationships
                .ToDictionary(item => item.AppId, StringComparer.OrdinalIgnoreCase);

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
                var preparedConfiguration = await PrepareConfigurationAsync(
                    app,
                    configuration,
                    secretConfiguration,
                    recipeId is null,
                    cancellationToken);
                createdSecretReferences.AddRange(preparedConfiguration.SecretReferences.Values);
                var installation = BuildInstallationEntity(
                    deployment,
                    app,
                    storagePaths,
                    AddStandaloneRouteConfiguration(preparedConfiguration.Configuration, configuration, app, recipeId),
                    preparedConfiguration.SecretReferences,
                    ResolveNetworkMode(
                        deployment.Id,
                        app,
                        relationships,
                        configuration,
                        reusableVpnGateway?.ContainerName),
                    recipeId is not null,
                    now);
                var run = await RunContainerAsync(
                    installation,
                    app,
                    DeserializeBindings(installation.VolumeMappingsJson),
                    DeserializeDictionary(installation.ConfigurationJson),
                    DeserializeDictionary(installation.SecretConfigurationJson),
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

                await EnsureCaddyAccessAsync(installation, cancellationToken);
                if (installation.CaddyRouteId.HasValue)
                {
                    createdCaddyRoutes.Add(installation.CaddyRouteId.Value);
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

            foreach (var routeId in createdCaddyRoutes)
            {
                try { await caddyIntegrationService.DeleteRouteAsync(routeId, cancellationToken); }
                catch (Exception cleanupException) { logger.LogDebug(cleanupException, "Could not remove Home Lab Caddy route {RouteId}.", routeId); }
            }

            foreach (var containerName in createdContainers)
            {
                try { await RunDockerAsync(["rm", "--force", containerName], $"Clean up Home Lab container {containerName}", cancellationToken); }
                catch (Exception cleanupException) { logger.LogDebug(cleanupException, "Could not clean up Home Lab container {ContainerName}.", containerName); }
            }

            try { await RunDockerAsync(["network", "rm", networkName], $"Clean up Home Lab network {networkName}", cancellationToken); }
            catch (Exception cleanupException) { logger.LogDebug(cleanupException, "Could not clean up Home Lab network {NetworkName}.", networkName); }
            foreach (var secretReference in createdSecretReferences.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try { await secretStore.DeleteSecretAsync(secretReference, cancellationToken); }
                catch (Exception cleanupException) { logger.LogDebug(cleanupException, "Could not remove Home Lab secret reference."); }
            }
            return Failure("Home Lab installation failed.", exception.Message, output, HomeLabHealthState.Failed);
        }
    }

    private HomeLabInstallationEntity BuildInstallationEntity(
        HomeLabDeploymentEntity deployment,
        HomeLabAppManifest app,
        IReadOnlyDictionary<string, string>? storagePaths,
        IReadOnlyDictionary<string, string> configuration,
        IReadOnlyDictionary<string, string> secretReferences,
        string networkMode,
        bool isRecipe,
        DateTimeOffset now)
    {
        var bindings = app.Volumes.Select(volume => new HomeLabVolumeBinding(
            ResolveVolumeHostPath(deployment.Id, app, volume, storagePaths),
            volume.ContainerPath,
            volume.ReadOnly)).ToArray();
        return new HomeLabInstallationEntity
        {
            Id = Guid.NewGuid(),
            DeploymentId = deployment.Id,
            AppId = app.Id,
            DisplayName = app.Id.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase) &&
                          configuration.TryGetValue("gateway-name", out var gatewayName) &&
                          !string.IsNullOrWhiteSpace(gatewayName)
                ? gatewayName.Trim()
                : app.Name,
            ContainerName = BuildContainerName(deployment.Id, app.Id),
            NetworkName = deployment.NetworkName,
            Image = $"{app.ImageRepository}:{app.ImageTag}",
            VolumeMappingsJson = JsonSerializer.Serialize(bindings, JsonOptions),
            ConfigurationJson = JsonSerializer.Serialize(configuration, JsonOptions),
            SecretConfigurationJson = JsonSerializer.Serialize(secretReferences, JsonOptions),
            NetworkMode = networkMode,
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
        IReadOnlyDictionary<string, string> secretReferences,
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

        await EnsureApplicationConfigurationAsync(app, bindings, cancellationToken);

        var args = new List<string>
        {
            "run", "--detach", "--name", installation.ContainerName,
            "--restart", "unless-stopped",
            "--label", "com.linuxmadesane.homelab=true",
            "--label", $"com.linuxmadesane.homelab.app={app.Id}",
            "--label", $"com.linuxmadesane.homelab.deployment={installation.DeploymentId}"
        };
        if (installation.NetworkMode.Equals("bridge", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["--network", installation.NetworkName, "--network-alias", app.Id]);
            if (app.Id.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var routedApp in HomeLabCatalog.Apps.Where(candidate => candidate.SupportsVpnGateway))
                {
                    args.AddRange(["--network-alias", routedApp.Id]);
                }
            }
        }
        else
        {
            args.AddRange(["--network", installation.NetworkMode]);
        }
        foreach (var capability in app.DockerCapabilities ?? [])
        {
            args.AddRange(["--cap-add", capability]);
        }

        foreach (var device in app.DockerDevices ?? [])
        {
            args.AddRange(["--device", device]);
        }
        var resolvedSecrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var secretReference in secretReferences)
        {
            var secret = await secretStore.ResolveSecretAsync(secretReference.Value, cancellationToken);
            if (string.IsNullOrEmpty(secret))
            {
                return ContainerRunResult.Failed(Failure("Home Lab secret resolution failed.", $"The secret for '{secretReference.Key}' is unavailable. Re-enter the VPN credentials.", output, HomeLabHealthState.Failed));
            }
            if (secretReference.Key.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase))
            {
                var containerPath = secretReference.Key["FILE:".Length..];
                var hostPath = ResolveSecretFileHostPath(bindings, containerPath);
                await WriteSecretFileAsync(hostPath, secret, cancellationToken);
                continue;
            }
            resolvedSecrets[secretReference.Key] = secret;
        }

        foreach (var environment in app.Environment
                     .Concat(configuration.Where(item => !IsInternalConfigurationKey(item.Key)))
                     .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.Last()))
        {
            args.AddRange(["--env", $"{environment.Key}={environment.Value}"]);
        }

        foreach (var binding in bindings)
        {
            args.AddRange(["--volume", $"{binding.HostPath}:{binding.ContainerPath}{(binding.ReadOnly ? ":ro" : string.Empty)}"]);
        }

        if (installation.NetworkMode.Equals("bridge", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var port in app.Ports)
            {
                args.AddRange(["--publish", $"127.0.0.1::{port.ContainerPort}"]);
            }
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

        var sensitiveIndexes = new HashSet<int>();
        foreach (var environment in resolvedSecrets)
        {
            var index = args.Count;
            args.AddRange(["--env", $"{environment.Key}={environment.Value}"]);
            sensitiveIndexes.Add(index + 1);
        }

        args.Add(installation.Image);
        var run = await RunDockerAsync(args, $"Install Home Lab app {app.Name}", cancellationToken, sensitiveIndexes);
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

        var ports = installation.NetworkMode.Equals("bridge", StringComparison.OrdinalIgnoreCase)
            ? ParsePortBindings(inspect, app)
            : await ParseSharedNamespacePortBindingsAsync(installation.NetworkMode, app, cancellationToken);
        if (installation.NetworkMode.Equals("bridge", StringComparison.OrdinalIgnoreCase) && ports.Count != app.Ports.Count)
        {
            return ContainerRunResult.Failed(Failure("Home Lab port inspection failed.", "The container was created but LMS could not resolve all published ports.", output, HomeLabHealthState.Failed));
        }

        return ContainerRunResult.Completed(ports);
    }

    private async Task<bool> EnsureApplicationConfigurationAsync(
        HomeLabAppManifest app,
        IReadOnlyList<HomeLabVolumeBinding> bindings,
        CancellationToken cancellationToken)
    {
        if (!app.Id.Equals("qbittorrent", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var configBinding = bindings.FirstOrDefault(binding =>
            binding.ContainerPath.Equals("/config", StringComparison.OrdinalIgnoreCase));
        if (configBinding is null)
        {
            throw new InvalidOperationException("qBittorrent requires a mounted /config volume for LMS proxy compatibility settings.");
        }

        var configFile = Path.Combine(configBinding.HostPath, "qBittorrent", "qBittorrent.conf");
        var quotedConfigFile = ShellQuote(configFile);
        var script = $"""
            set -eu
            config_file={quotedConfigFile}
            config_dir=$(dirname "$config_file")
            mkdir -p "$config_dir"
            changed=0
            if [ ! -f "$config_file" ]; then
                cat > "$config_file" <<'LMS_QBITTORRENT_CONFIG'
            [Preferences]
            WebUI\ReverseProxySupportEnabled=true
            WebUI\HostHeaderValidation=false
            LMS_QBITTORRENT_CONFIG
                changed=1
            else
                if ! grep -q '^WebUI\\ReverseProxySupportEnabled=true$' "$config_file"; then
                    if grep -q '^WebUI\\ReverseProxySupportEnabled=' "$config_file"; then
                        sed -i 's/^WebUI\\ReverseProxySupportEnabled=.*/WebUI\\ReverseProxySupportEnabled=true/' "$config_file"
                    elif grep -q '^\\[Preferences\\]$' "$config_file"; then
                        sed -i '/^\\[Preferences\\]$/a WebUI\\ReverseProxySupportEnabled=true' "$config_file"
                    else
                        printf '\n[Preferences]\nWebUI\ReverseProxySupportEnabled=true\n' >> "$config_file"
                    fi
                    changed=1
                fi
                if ! grep -q '^WebUI\\HostHeaderValidation=false$' "$config_file"; then
                    if grep -q '^WebUI\\HostHeaderValidation=' "$config_file"; then
                        sed -i 's/^WebUI\\HostHeaderValidation=.*/WebUI\\HostHeaderValidation=false/' "$config_file"
                    elif grep -q '^\\[Preferences\\]$' "$config_file"; then
                        sed -i '/^\\[Preferences\\]$/a WebUI\\HostHeaderValidation=false' "$config_file"
                    else
                        printf '\n[Preferences]\nWebUI\HostHeaderValidation=false\n' >> "$config_file"
                    fi
                    changed=1
                fi
            fi
            chown 1000:1000 "$config_file"
            printf '%s\n' "$changed"
            """;
        var result = await RunAsync(
            new LinuxCommandRequest(
                "bash",
                ["-c", script],
                true,
                TimeSpan.FromSeconds(30),
                "Configure qBittorrent for LMS browser access"),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"qBittorrent proxy compatibility settings could not be applied: {NormalizeFailure(result)}");
        }

        return result.StandardOutput.Trim().EndsWith("1", StringComparison.Ordinal);
    }

    private async Task<LinuxCommandResult> ResetQbittorrentCredentialsAsync(
        IReadOnlyList<HomeLabVolumeBinding> bindings,
        CancellationToken cancellationToken)
    {
        var configBinding = bindings.FirstOrDefault(binding =>
            binding.ContainerPath.Equals("/config", StringComparison.OrdinalIgnoreCase));
        if (configBinding is null)
        {
            throw new InvalidOperationException("qBittorrent requires a mounted /config volume for credential reset.");
        }

        var configFile = Path.Combine(configBinding.HostPath, "qBittorrent", "qBittorrent.conf");
        var script = $"""
            set -eu
            config_file={ShellQuote(configFile)}
            if [ ! -f "$config_file" ]; then
                printf 'qBittorrent configuration was not found at %s\n' "$config_file" >&2
                exit 1
            fi
            if grep -q '^WebUI\\Username=' "$config_file"; then
                sed -i 's/^WebUI\\Username=.*/WebUI\\Username=admin/' "$config_file"
            elif grep -q '^\\[Preferences\\]$' "$config_file"; then
                sed -i '/^\\[Preferences\\]$/a WebUI\Username=admin' "$config_file"
            else
                printf '\n[Preferences]\nWebUI\Username=admin\n' >> "$config_file"
            fi
            sed -i '/^WebUI\\Password_PBKDF2=/d; /^WebUI\\Password_ha1=/d; /^WebUI\\Password=/d' "$config_file"
            chown 1000:1000 "$config_file"
            printf 'qBittorrent password hash removed.\n'
            """;
        return await RunAsync(
            new LinuxCommandRequest(
                "bash",
                ["-c", script],
                true,
                TimeSpan.FromSeconds(30),
                "Reset qBittorrent Web UI credentials"),
            cancellationToken);
    }

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\\\"'\\\"'", StringComparison.Ordinal)}'";

    private async Task<IReadOnlyList<HomeLabPortBinding>> ParseSharedNamespacePortBindingsAsync(
        string networkMode,
        HomeLabAppManifest app,
        CancellationToken cancellationToken)
    {
        var gatewayName = networkMode["container:".Length..];
        var gateway = await InspectContainerAsync(gatewayName, cancellationToken);
        return gateway is null
            ? []
            : ParsePortBindings(gateway, app);
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
        var app = HomeLabCatalog.GetApp(installation.AppId);
        if (app.RequiresVpnGateway &&
            !installation.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase))
        {
            installation.HealthState = (int)HomeLabHealthState.Blocked;
            installation.HealthDetail = $"Blocked: {app.Name} requires VPN Gateway (Gluetun) routing.";
            installation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            return;
        }

        if (installation.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase))
        {
            var gatewayName = installation.NetworkMode["container:".Length..];
            var gateway = await InspectContainerAsync(gatewayName, cancellationToken);
            if (gateway is null)
            {
                installation.HealthState = (int)HomeLabHealthState.Blocked;
                installation.HealthDetail = "Blocked: VPN Gateway container is unavailable.";
                installation.UpdatedAtUtc = DateTimeOffset.UtcNow;
                return;
            }

            var gatewayHealth = ResolveHealth(gateway);
            if (gatewayHealth.Item1 is not (HomeLabHealthState.Healthy or HomeLabHealthState.Starting))
            {
                installation.HealthState = (int)HomeLabHealthState.Blocked;
                installation.HealthDetail = $"Blocked: VPN Gateway unavailable ({gatewayHealth.Item2}).";
                installation.UpdatedAtUtc = DateTimeOffset.UtcNow;
                return;
            }
        }

        var inspect = await InspectContainerAsync(installation.ContainerName, cancellationToken);
        var health = inspect is null
            ? (HomeLabHealthState.Failed, "Container could not be inspected.")
            : ResolveHealth(inspect);
        installation.HealthState = (int)health.Item1;
        installation.HealthDetail = health.Item2;
        installation.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private async Task EnforceRequiredVpnRouteAsync(
        HomeLabInstallationEntity installation,
        HomeLabAppManifest app,
        CancellationToken cancellationToken)
    {
        if (!app.RequiresVpnGateway ||
            installation.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var inspect = await InspectContainerAsync(installation.ContainerName, cancellationToken);
        var isRunning = inspect?["State"]?["Running"]?.GetValue<bool>() == true;
        if (isRunning)
        {
            var stop = await RunDockerAsync(
                ["stop", installation.ContainerName],
                $"Stop unsafe direct {app.Name} container",
                cancellationToken);
            if (stop.ExitCode != 0)
            {
                logger.LogWarning(
                    "Could not stop unsafe direct Home Lab app {AppId}: {Error}",
                    app.Id,
                    NormalizeFailure(stop));
            }
        }
    }

    private async Task<string?> ResolvePublicIpAsync(
        string containerName,
        CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync(
            [
                "exec",
                containerName,
                "sh",
                "-c",
                "if command -v wget >/dev/null 2>&1; then wget -qO- -T 10 https://api.ipify.org; elif command -v curl >/dev/null 2>&1; then curl -fsS --max-time 10 https://api.ipify.org; else exit 127; fi"
            ],
            $"Verify public IP through Home Lab container {containerName}",
            cancellationToken);
        if (result.ExitCode != 0)
        {
            return null;
        }

        var candidate = result.StandardOutput
            .Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return IPAddress.TryParse(candidate, out _) ? candidate : null;
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
        CancellationToken cancellationToken,
        IReadOnlySet<int>? sensitiveArgumentIndexes = null) =>
        await RunAsync(
            new LinuxCommandRequest("docker", arguments, true, DockerCommandTimeout, description)
            {
                IsOptionalExternalTool = true,
                SensitiveArgumentIndexes = sensitiveArgumentIndexes
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

    private async Task<PreparedConfiguration> PrepareConfigurationAsync(
        HomeLabAppManifest app,
        IReadOnlyDictionary<string, string>? configuration,
        IReadOnlyDictionary<string, string>? secretConfiguration,
        bool requireStandaloneNetworkRoute,
        CancellationToken cancellationToken)
    {
        var supplied = configuration ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var suppliedSecrets = secretConfiguration ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, string>(app.Environment, StringComparer.OrdinalIgnoreCase);
        var secretReferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (app.Id.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase))
        {
            if (supplied.TryGetValue("gateway-name", out var suppliedGatewayName) && !string.IsNullOrWhiteSpace(suppliedGatewayName))
            {
                values["gateway-name"] = suppliedGatewayName.Trim();
            }

            var setupMode = supplied.TryGetValue("configuration-mode", out var suppliedMode) && !string.IsNullOrWhiteSpace(suppliedMode)
                ? suppliedMode.Trim()
                : "Guided";
            var protocol = RequiredValue(supplied, "protocol", "Choose a VPN protocol.");
            var protocolValue = protocol.Equals("WireGuard", StringComparison.OrdinalIgnoreCase) ? "wireguard" :
                protocol.Equals("OpenVPN", StringComparison.OrdinalIgnoreCase) ? "openvpn" :
                throw new InvalidOperationException("Choose WireGuard or OpenVPN.");

            if (setupMode.Equals("Paste provider config", StringComparison.OrdinalIgnoreCase))
            {
                values["VPN_SERVICE_PROVIDER"] = "custom";
                values["VPN_TYPE"] = protocolValue;
                if (!suppliedSecrets.TryGetValue("vpn-config", out var pastedConfig) || string.IsNullOrWhiteSpace(pastedConfig))
                {
                    throw new InvalidOperationException("Paste the provider configuration file.");
                }

                var configPath = protocolValue == "openvpn"
                    ? "/gluetun/custom.conf"
                    : "/gluetun/wireguard/wg0.conf";
                secretReferences[$"FILE:{configPath}"] = await secretStore.StoreSecretAsync(
                    pastedConfig,
                    $"Home Lab VPN Gateway: pasted {protocolValue} provider configuration",
                    cancellationToken);
                if (protocolValue == "openvpn")
                {
                    values["OPENVPN_CUSTOM_CONFIG"] = configPath;
                    await AddOptionalSecretAsync(secretReferences, suppliedSecrets, "openvpn-username", "OPENVPN_USER", cancellationToken);
                    await AddOptionalSecretAsync(secretReferences, suppliedSecrets, "openvpn-password", "OPENVPN_PASSWORD", cancellationToken);
                }
                return new PreparedConfiguration(values, secretReferences);
            }

            var provider = RequiredValue(supplied, "provider", "Choose a VPN provider.");
            var providerValue = provider switch
            {
                "ProtonVPN" => "protonvpn",
                "NordVPN" => "nordvpn",
                "Mullvad" => "mullvad",
                "Surfshark" => "surfshark",
                "Private Internet Access" => "private internet access",
                "Custom WireGuard" or "Custom OpenVPN" => "custom",
                _ => throw new InvalidOperationException($"VPN provider '{provider}' is not supported by this LMS definition.")
            };

            if (provider.Equals("Custom WireGuard", StringComparison.OrdinalIgnoreCase) && protocolValue != "wireguard" ||
                provider.Equals("Custom OpenVPN", StringComparison.OrdinalIgnoreCase) && protocolValue != "openvpn")
            {
                throw new InvalidOperationException($"{provider} must use {provider.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) switch { true => "WireGuard", _ => "OpenVPN" }}.");
            }

            values["VPN_SERVICE_PROVIDER"] = providerValue;
            values["VPN_TYPE"] = protocolValue;
            AddOptional(values, supplied, "server-countries", "SERVER_COUNTRIES");

            if (protocolValue == "wireguard")
            {
                await AddRequiredSecretAsync(secretReferences, suppliedSecrets, "wireguard-private-key", "WIREGUARD_PRIVATE_KEY", "Enter the WireGuard private key.", cancellationToken);
                AddRequired(values, supplied, "wireguard-addresses", "WIREGUARD_ADDRESSES", "Enter the WireGuard addresses, for example 10.2.0.2/32.");
                if (provider.Equals("Custom WireGuard", StringComparison.OrdinalIgnoreCase))
                {
                    AddRequired(values, supplied, "wireguard-public-key", "WIREGUARD_PUBLIC_KEY", "Enter the WireGuard server public key.");
                    AddRequired(values, supplied, "wireguard-endpoint-ip", "WIREGUARD_ENDPOINT_IP", "Enter the WireGuard endpoint IP.");
                    AddRequired(values, supplied, "wireguard-endpoint-port", "WIREGUARD_ENDPOINT_PORT", "Enter the WireGuard endpoint port.");
                }
                else
                {
                    AddOptional(values, supplied, "wireguard-public-key", "WIREGUARD_PUBLIC_KEY");
                    AddOptional(values, supplied, "wireguard-endpoint-ip", "WIREGUARD_ENDPOINT_IP");
                    AddOptional(values, supplied, "wireguard-endpoint-port", "WIREGUARD_ENDPOINT_PORT");
                }
                await AddOptionalSecretAsync(secretReferences, suppliedSecrets, "wireguard-preshared-key", "WIREGUARD_PRESHARED_KEY", cancellationToken);
            }
            else
            {
                await AddRequiredSecretAsync(secretReferences, suppliedSecrets, "openvpn-username", "OPENVPN_USER", "Enter the OpenVPN username.", cancellationToken);
                await AddRequiredSecretAsync(secretReferences, suppliedSecrets, "openvpn-password", "OPENVPN_PASSWORD", "Enter the OpenVPN password.", cancellationToken);
            }

            if (provider.Equals("Custom OpenVPN", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Custom OpenVPN needs a mounted .ovpn profile. Use a supported Gluetun provider here, or add the profile to the LMS host before selecting this option.");
            }
        }
        else
        {
            if (app.SupportsVpnGateway && (requireStandaloneNetworkRoute || supplied.ContainsKey("network-route")))
            {
                var route = RequiredValue(
                    supplied,
                    "network-route",
                    $"Choose an Internet route for {app.Name}: VPN Gateway (Gluetun) or Direct (no VPN).");
                if (!route.Equals("VPN Gateway (Gluetun)", StringComparison.OrdinalIgnoreCase) &&
                    !route.Equals("Direct (no VPN)", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"The Internet route '{route}' is not supported for {app.Name}.");
                }
                if (app.RequiresVpnGateway && route.Equals("Direct (no VPN)", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"{app.Name} must use VPN Gateway (Gluetun). Direct routing is blocked for safety.");
                }
            }

            foreach (var item in supplied)
            {
                var schema = app.ConfigurationSchema.FirstOrDefault(field => field.Id.Equals(item.Key, StringComparison.OrdinalIgnoreCase));
                if (schema is not null && !schema.Secret && !IsInternalConfigurationKey(schema.Id))
                {
                    values[schema.Id] = item.Value ?? string.Empty;
                }
            }
        }

        foreach (var item in suppliedSecrets)
        {
            var field = app.ConfigurationSchema.FirstOrDefault(candidate => candidate.Id.Equals(item.Key, StringComparison.OrdinalIgnoreCase));
            if (field is null || !field.Secret || app.Id.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(item.Value))
            {
                throw new InvalidOperationException($"Enter {field.Label}.");
            }
            secretReferences[field.Id] = await secretStore.StoreSecretAsync(item.Value, $"Home Lab {app.Name}: {field.Label}", cancellationToken);
        }

        return new PreparedConfiguration(values, secretReferences);
    }

    private async Task AddRequiredSecretAsync(
        IDictionary<string, string> references,
        IReadOnlyDictionary<string, string> supplied,
        string fieldId,
        string environmentName,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        if (!supplied.TryGetValue(fieldId, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(errorMessage);
        }
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new InvalidOperationException($"{fieldId} cannot contain line breaks.");
        }
        references[environmentName] = await secretStore.StoreSecretAsync(value, $"Home Lab VPN Gateway: {fieldId}", cancellationToken);
    }

    private async Task AddOptionalSecretAsync(
        IDictionary<string, string> references,
        IReadOnlyDictionary<string, string> supplied,
        string fieldId,
        string environmentName,
        CancellationToken cancellationToken)
    {
        if (!supplied.TryGetValue(fieldId, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new InvalidOperationException($"{fieldId} cannot contain line breaks.");
        }
        references[environmentName] = await secretStore.StoreSecretAsync(value, $"Home Lab VPN Gateway: {fieldId}", cancellationToken);
    }

    private static string ResolveSecretFileHostPath(
        IReadOnlyList<HomeLabVolumeBinding> bindings,
        string containerPath)
    {
        var normalizedContainerPath = containerPath.Trim();
        var binding = bindings
            .Where(item => normalizedContainerPath.Equals(item.ContainerPath, StringComparison.Ordinal) ||
                           normalizedContainerPath.StartsWith(item.ContainerPath.TrimEnd('/') + "/", StringComparison.Ordinal))
            .OrderByDescending(item => item.ContainerPath.Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"The VPN configuration path '{containerPath}' is not inside a mounted LMS volume.");
        var relativePath = normalizedContainerPath[binding.ContainerPath.TrimEnd('/').Length..].TrimStart('/');
        if (relativePath.Length == 0 || relativePath.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The VPN configuration file path is invalid.");
        }
        var hostRoot = Path.GetFullPath(binding.HostPath);
        var hostPath = Path.GetFullPath(Path.Combine(hostRoot, relativePath));
        var rootWithSeparator = hostRoot.EndsWith(Path.DirectorySeparatorChar) ? hostRoot : hostRoot + Path.DirectorySeparatorChar;
        if (!hostPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The VPN configuration file path escapes its LMS volume.");
        }
        return hostPath;
    }

    private async Task WriteSecretFileAsync(string hostPath, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(hostPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var mkdir = await RunAsync(
                new LinuxCommandRequest(
                    "mkdir",
                    ["-p", directory],
                    true,
                    TimeSpan.FromSeconds(30),
                    $"Prepare Home Lab secret file directory {Path.GetFileName(directory)}"),
                cancellationToken);
            EnsureSuccess(mkdir, "The VPN configuration directory could not be prepared.");
        }

        var write = await RunAsync(
            new LinuxCommandRequest(
                "dd",
                [$"of={hostPath}", "status=none"],
                true,
                TimeSpan.FromSeconds(30),
                $"Write Home Lab secret file {Path.GetFileName(hostPath)}")
            {
                StandardInputBytes = new System.Text.UTF8Encoding(false).GetBytes(content)
            },
            cancellationToken);
        EnsureSuccess(write, "The VPN provider configuration could not be written.");

        var chmod = await RunAsync(
            new LinuxCommandRequest(
                "chmod",
                ["600", hostPath],
                true,
                TimeSpan.FromSeconds(30),
                $"Protect Home Lab secret file {Path.GetFileName(hostPath)}"),
            cancellationToken);
        EnsureSuccess(chmod, "The VPN provider configuration permissions could not be secured.");

    }

    private sealed record PreparedConfiguration(
        IReadOnlyDictionary<string, string> Configuration,
        IReadOnlyDictionary<string, string> SecretReferences);

    private static string RequiredValue(IReadOnlyDictionary<string, string> values, string key, string errorMessage) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new InvalidOperationException(errorMessage);

    private static void AddRequired(IDictionary<string, string> target, IReadOnlyDictionary<string, string> source, string sourceKey, string targetKey, string errorMessage)
    {
        target[targetKey] = RequiredValue(source, sourceKey, errorMessage);
    }

    private static void AddOptional(IDictionary<string, string> target, IReadOnlyDictionary<string, string> source, string sourceKey, string targetKey)
    {
        if (source.TryGetValue(sourceKey, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            target[targetKey] = value.Trim();
        }
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

    private static string ResolveNetworkMode(
        Guid deploymentId,
        HomeLabAppManifest app,
        IReadOnlyDictionary<string, HomeLabRecipeRelationship> relationships,
        IReadOnlyDictionary<string, string>? configuration,
        string? reusableVpnGatewayContainerName)
    {
        var routeVia = relationships.TryGetValue(app.Id, out var relationship)
            ? relationship.RouteVia
            : IsVpnRouteSelected(configuration)
                ? "vpn-gateway"
                : null;
        if (string.IsNullOrWhiteSpace(routeVia))
        {
            return "bridge";
        }

        var gateway = HomeLabCatalog.GetApp(routeVia);
        if (!gateway.IsInstallable)
        {
            throw new InvalidOperationException($"The network gateway '{gateway.Name}' is not installable.");
        }

        var gatewayContainerName = routeVia.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase) &&
                                   !string.IsNullOrWhiteSpace(reusableVpnGatewayContainerName)
            ? reusableVpnGatewayContainerName
            : BuildContainerName(deploymentId, gateway.Id);
        return $"container:{gatewayContainerName}";
    }

    private static bool IsVpnRouteSelected(IReadOnlyDictionary<string, string>? configuration) =>
        configuration?.TryGetValue("network-route", out var route) == true &&
        route.Equals("VPN Gateway (Gluetun)", StringComparison.OrdinalIgnoreCase);

    private static Guid? ResolveVpnGatewayId(IReadOnlyDictionary<string, string>? configuration) =>
        configuration?.TryGetValue("vpn-gateway", out var value) == true &&
        Guid.TryParse(value, out var gatewayId)
            ? gatewayId
            : null;

    private static bool IsInternalConfigurationKey(string key) =>
        key.Equals("network-route", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("gateway-name", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> AddStandaloneRouteConfiguration(
        IReadOnlyDictionary<string, string> preparedConfiguration,
        IReadOnlyDictionary<string, string>? suppliedConfiguration,
        HomeLabAppManifest app,
        string? recipeId)
    {
        if (recipeId is not null || !app.SupportsVpnGateway || suppliedConfiguration is null)
        {
            return preparedConfiguration;
        }

        var persisted = new Dictionary<string, string>(preparedConfiguration, StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "network-route", "vpn-gateway" })
        {
            if (suppliedConfiguration.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                persisted[key] = value.Trim();
            }
        }

        return persisted;
    }

    private static void PersistNetworkRouteConfiguration(
        HomeLabInstallationEntity installation,
        bool useVpnGateway,
        Guid? gatewayInstallationId)
    {
        var configuration = DeserializeDictionary(installation.ConfigurationJson);
        configuration["network-route"] = useVpnGateway ? "VPN Gateway (Gluetun)" : "Direct (no VPN)";
        if (useVpnGateway && gatewayInstallationId is Guid gatewayId)
        {
            configuration["vpn-gateway"] = gatewayId.ToString();
        }
        else
        {
            configuration.Remove("vpn-gateway");
        }

        installation.ConfigurationJson = JsonSerializer.Serialize(configuration, JsonOptions);
    }

    private async Task<HomeLabInstallationEntity?> ResolveVpnGatewayAsync(
        Guid? gatewayInstallationId,
        CancellationToken cancellationToken)
    {
        var gateways = await dbContext.HomeLabInstallations
            .AsNoTracking()
            .Where(item => item.AppId == "vpn-gateway")
            .ToListAsync(cancellationToken);
        if (gatewayInstallationId is Guid requestedId)
        {
            var requested = gateways.FirstOrDefault(item => item.Id == requestedId);
            if (requested is null)
            {
                throw new InvalidOperationException("The selected VPN Gateway installation no longer exists.");
            }
            return requested;
        }

        return gateways.Count switch
        {
            0 => null,
            1 => gateways[0],
            _ => throw new InvalidOperationException("Multiple VPN Gateways are installed. Select the gateway this app should use.")
        };
    }

    private async Task EnsureVpnNamespacePortAvailabilityAsync(
        HomeLabAppManifest app,
        string gatewayContainerName,
        Guid? excludedInstallationId,
        CancellationToken cancellationToken)
    {
        var networkMode = $"container:{gatewayContainerName}";
        var routedInstallations = await dbContext.HomeLabInstallations
            .AsNoTracking()
            .Where(item => item.NetworkMode == networkMode &&
                           (!excludedInstallationId.HasValue || item.Id != excludedInstallationId.Value))
            .ToArrayAsync(cancellationToken);

        foreach (var existing in routedInstallations)
        {
            var existingApp = HomeLabCatalog.GetApp(existing.AppId);
            var conflictingPort = app.Ports.FirstOrDefault(port =>
                existingApp.Ports.Any(existingPort =>
                    existingPort.ContainerPort == port.ContainerPort &&
                    existingPort.Protocol.Equals(port.Protocol, StringComparison.OrdinalIgnoreCase)));
            if (conflictingPort is not null)
            {
                throw new InvalidOperationException(
                    $"{app.Name} cannot share the VPN Gateway network namespace with {existingApp.Name}: both require {conflictingPort.Protocol.ToUpperInvariant()} port {conflictingPort.ContainerPort}. Choose a direct route or use a separate VPN Gateway installation.");
            }
        }
    }

    private static IReadOnlyList<string> ExpandDependencies(IReadOnlyList<string> requestedAppIds)
    {
        var result = new List<string>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string appId)
        {
            if (!visiting.Add(appId))
            {
                throw new InvalidOperationException($"The Home Lab catalog contains a dependency cycle involving '{appId}'.");
            }

            var app = HomeLabCatalog.GetApp(appId);
            foreach (var dependency in app.Dependencies)
            {
                Visit(dependency);
            }

            visiting.Remove(appId);
            if (added.Add(appId))
            {
                result.Add(appId);
            }
        }

        foreach (var appId in requestedAppIds)
        {
            Visit(appId);
        }

        return result;
    }

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
            item.NetworkMode,
            item.EdgeGatewayRouteId,
            item.CaddyRouteId,
            item.CaddySourcePort,
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
