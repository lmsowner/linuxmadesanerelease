// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Text.Json;
using System.Text.Json.Nodes;
using LinuxMadeSane.Application.Contracts.HomeLab;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed partial class HomeLabService
{
    internal static bool SupportsPortEditing(HomeLabInstallationEntity install) =>
        !IsExternallyManaged(install) && install.NetworkMode == "bridge" && install.AppId != "vpn-gateway" &&
        !HomeLabCatalog.GetApp(install.AppId).RequiresVpnGateway;

    private async Task<JsonObject> VerifyContainerOwnerAsync(HomeLabInstallationEntity install, string id, CancellationToken token)
    {
        EnsureLmsOwns(install);
        var inspect = await InspectContainerAsync(install.ContainerName, token);
        if (inspect is null || inspect["Id"]?.ToString() != id || inspect["Config"]?["Labels"]?["com.docker.compose.project"] != null ||
            inspect["Config"]?["Labels"]?["com.linuxmadesane.homelab.deployment"]?.ToString() != install.DeploymentId.ToString())
            throw new InvalidOperationException("Container ownership changed. Refresh the list.");
        return inspect;
    }

    public async Task UpdatePublishedPortsAsync(Guid installationId, string containerId,
        IReadOnlyList<DockerPortBinding> bindings, CancellationToken cancellationToken = default)
    {
        LocalContainerService.ValidatePorts(bindings);
        var install = await dbContext.HomeLabInstallations.Include(i => i.ServiceEndpoints)
            .SingleAsync(i => i.Id == installationId, cancellationToken);
        await dbContext.Entry(install).ReloadAsync(cancellationToken);
        EnsureLmsOwns(install);
        if (!SupportsPortEditing(install) || await dbContext.HomeLabInstallations.AnyAsync(
            i => i.NetworkMode == "container:" + install.ContainerName, cancellationToken))
            throw new InvalidOperationException("Use Home Lab network settings for this shared namespace.");
        var app = HomeLabCatalog.GetApp(install.AppId);
        var original = await VerifyContainerOwnerAsync(install, containerId, cancellationToken);
        var previousConfig = install.ConfigurationJson;
        CaptureBrowserEntryMetadata(install, original);
        var config = DeserializeDictionary(install.ConfigurationJson);
        install.ConfigurationJson = previousConfig;
        if (bindings.Count != app.Ports.Count) throw new InvalidOperationException("Keep the ports declared by this Home Lab app.");
        foreach (var manifest in app.Ports)
        {
            var binding = bindings.SingleOrDefault(p => p.ContainerPort == manifest.ContainerPort && p.Protocol == manifest.Protocol)
                ?? throw new InvalidOperationException("Keep each declared container port and protocol.");
            if (manifest.HostBindingAddress is not ("0.0.0.0" or "::") && binding.Address != "127.0.0.1")
                throw new InvalidOperationException("This Home Lab port requires localhost.");
            if (binding.Address is not ("0.0.0.0" or "::")) ValidateListenAddress(binding.Address);
            config["lms-host-port-" + manifest.Name] = binding.HostPort.ToString();
            config["lms-host-address-" + manifest.Name] = binding.Address;
        }
        var running = original["State"]!["Running"]!.GetValue<bool>();
        var tag = Guid.NewGuid().ToString("N");
        async Task Docker(params string[] args)
        {
            var result = await RunDockerAsync(args, "Apply Home Lab published ports", CancellationToken.None);
            if (result.ExitCode != 0) throw new InvalidOperationException("Docker could not apply the Home Lab port change.");
        }
        var previousPorts = install.PortMappingsJson;
        var renamed = false;
        try
        {
            if (running) await Docker("stop", containerId);
            await Docker("rename", containerId, "lms-port-backup-" + containerId[..16]);
            renamed = true;
            install.ConfigurationJson = JsonSerializer.Serialize(config, JsonOptions);
            var run = await RunContainerAsync(install, app, DeserializeBindings(install.VolumeMappingsJson), config,
                DeserializeDictionary(install.SecretConfigurationJson), [], cancellationToken, tag, original["Image"]!.ToString());
            if (!run.Succeeded || !bindings.All(p => run.PortBindings.Any(b => b.ContainerPort == p.ContainerPort &&
                b.HostPort == p.HostPort && b.HostIp == p.Address)))
                throw new InvalidOperationException("Docker could not apply the new bindings. Check whether the host port is occupied.");
            var created = await InspectContainerAsync(install.ContainerName, cancellationToken);
            if (created?["Config"]?["Labels"]?["com.linuxmadesane.port-edit"]?.ToString() != tag)
                throw new InvalidOperationException("Replacement identity could not be verified.");
            if (!running) await Docker("stop", created["Id"]!.ToString());
            install.PortMappingsJson = JsonSerializer.Serialize(run.PortBindings, JsonOptions);
            await UpdateCaddyAccessAsync(install, app, run.PortBindings, cancellationToken);
            SynchronizeLocalEndpoints(install);
            await SynchronizePublishedEndpointAsync(install, cancellationToken);
            await RefreshHealthInternalAsync(install, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            install.ConfigurationJson = previousConfig;
            install.PortMappingsJson = previousPorts;
            if (renamed)
            {
                var created = await InspectContainerAsync(install.ContainerName, CancellationToken.None);
                if (created?["Config"]?["Labels"]?["com.linuxmadesane.port-edit"]?.ToString() == tag)
                    await Docker("rm", "-f", created["Id"]!.ToString());
                await Docker("rename", containerId, install.ContainerName);
            }
            if (running) await Docker("start", containerId);
            await UpdateCaddyAccessAsync(install, app, DeserializePortBindings(previousPorts), CancellationToken.None);
            SynchronizeLocalEndpoints(install);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        await Docker("rm", containerId);
    }
}
