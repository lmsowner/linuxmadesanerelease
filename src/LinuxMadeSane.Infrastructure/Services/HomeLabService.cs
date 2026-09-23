// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Xml.Linq;
using LinuxMadeSane.Application.Contracts.Caddy;
using LinuxMadeSane.Application.Contracts.EdgeGateway;
using LinuxMadeSane.Application.Contracts.HomeLab;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Application.Services.EdgeGateway;
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
    IEdgeGatewayRouteRegistrationService edgeGatewayRouteRegistrationService,
    ICaddyIntegrationService caddyIntegrationService,
    ISecretStore secretStore,
    ILogger<HomeLabService> logger) : IHomeLabService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DockerCommandTimeout = TimeSpan.FromMinutes(5);
    private const string ContainerSettingsBackupImageKey = "lms-container-backup-image";
    private const string ContainerSettingsBackupConfigurationKey = "lms-container-backup-configuration";
    private const string ContainerSettingsBackupVolumesKey = "lms-container-backup-volumes";
    private const string HomeLabFilesRole = "home-lab";
    private const string DefaultHomeLabFilesRoot = "/mnt/storage/home-lab";
    private const string StandardStorageHostPath = "/mnt/storage";
    private const string StandardStorageContainerPath = "/storage";
    private const int MaximumConfigFileBytes = 128 * 1024;
    private const int MaximumConfigInspectionBytes = 256 * 1024;
    private static readonly HashSet<string> InspectableConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".json", ".yml", ".yaml", ".conf", ".config", ".ini", ".toml", ".properties", ".txt"
    };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<HomeLabWorkspace> GetWorkspaceSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var installationEntities = await dbContext.HomeLabInstallations
            .Include(item => item.ServiceEndpoints)
            .OrderBy(item => item.DisplayName)
            .ToListAsync(cancellationToken);
        var deploymentEntities = await dbContext.HomeLabDeployments
            .AsNoTracking()
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
        var storageRoleEntities = await dbContext.HomeLabStorageRoles
            .AsNoTracking()
            .OrderBy(item => item.Role)
            .ToListAsync(cancellationToken);

        // Endpoint metadata is required to render access links and resolve recipe
        // dependencies. Older installations may predate the endpoint table, so repair
        // only this local metadata here without inspecting or changing containers.
        var endpointMetadataChanged = false;
        foreach (var entity in installationEntities.Where(NeedsLocalEndpointSynchronization))
        {
            SynchronizeLocalEndpoints(entity);
            endpointMetadataChanged = true;
        }

        if (endpointMetadataChanged)
        {
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
            {
                // Concurrent page loads can both discover the same missing metadata.
                // The in-memory snapshot is still complete for this request, and the
                // next snapshot will read the rows committed by the winning request.
                logger.LogDebug(exception, "Could not persist repaired Home Lab endpoint metadata.");
            }
        }

        return new HomeLabWorkspace(
            HomeLabCatalog.VisibleApps,
            HomeLabCatalog.Recipes,
            BuildStorageRoles(storageRoleEntities),
            deploymentEntities.Select(MapDeployment).ToArray(),
            installationEntities.Select(MapInstallation).ToArray());
    }

    private static bool NeedsLocalEndpointSynchronization(HomeLabInstallationEntity installation)
    {
        var app = HomeLabCatalog.GetApp(installation.AppId);
        if (HomeLabEndpointPlanner.IncludesScope(app, HomeLabEndpointScope.Internal) &&
            app.Ports.Any(port => installation.ServiceEndpoints.All(endpoint =>
                !endpoint.PortName.Equals(port.Name, StringComparison.OrdinalIgnoreCase) ||
                endpoint.Scope != (int)HomeLabEndpointScope.Internal)))
        {
            return true;
        }

        var clientAccess = HomeLabEndpointPlanner.ResolveClientAccess(app);
        var bindings = DeserializePortBindings(installation.PortMappingsJson);
        var hasClientBinding = clientAccess is not null &&
                               bindings.Any(port => port.Name.Equals(clientAccess.PortName, StringComparison.OrdinalIgnoreCase));
        if (!hasClientBinding || installation.CaddySourcePort is not > 0)
        {
            return false;
        }

        var clientEndpointMissing = installation.ServiceEndpoints.All(endpoint =>
            !endpoint.PortName.Equals(clientAccess!.PortName, StringComparison.OrdinalIgnoreCase) ||
            endpoint.Scope != (int)HomeLabEndpointScope.Client);
        var lanEndpointMissing = HomeLabEndpointPlanner.IncludesScope(app, HomeLabEndpointScope.Lan) &&
                                 installation.ServiceEndpoints.All(endpoint =>
                                     !endpoint.PortName.Equals(clientAccess!.PortName, StringComparison.OrdinalIgnoreCase) ||
                                     endpoint.Scope != (int)HomeLabEndpointScope.Lan);
        return clientEndpointMissing || lanEndpointMissing;
    }

    public async Task<HomeLabWorkspace> GetWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.HomeLabInstallations
            .Include(item => item.ServiceEndpoints)
            .OrderBy(item => item.DisplayName)
            .ToListAsync(cancellationToken);

        try
        {
            await EnsureBrowsableVolumeLayoutAsync(entities, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not prepare the browsable Home Lab files layout.");
        }

        foreach (var entity in entities)
        {
            try
            {
                var app = HomeLabCatalog.GetApp(entity.AppId);
                await EnforceRequiredVpnRouteAsync(entity, app, cancellationToken);
                var applicationConfigurationChanged = await EnsureApplicationConfigurationAsync(
                    entity,
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
                SynchronizeLocalEndpoints(entity);
                await SynchronizePublishedEndpointAsync(entity, cancellationToken);
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
        var storageRoles = BuildStorageRoles(storageRoleEntities);

        return new HomeLabWorkspace(HomeLabCatalog.VisibleApps, HomeLabCatalog.Recipes, storageRoles, deployments, installations);
    }

    public async Task<IReadOnlyList<HomeLabRuntimeHealth>> RefreshRuntimeHealthAsync(
        CancellationToken cancellationToken = default)
    {
        var installations = await dbContext.HomeLabInstallations
            .OrderBy(item => item.DisplayName)
            .ToListAsync(cancellationToken);
        foreach (var installation in installations)
        {
            await RefreshHealthInternalAsync(installation, cancellationToken);
        }

        if (installations.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return installations.Select(item => new HomeLabRuntimeHealth(
            item.Id,
            ToHealth(item.HealthState),
            item.HealthDetail,
            item.UpdatedAtUtc)).ToArray();
    }

    public async Task<IReadOnlyList<HomeLabServiceEndpoint>> RefreshEndpointHealthAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var installation = await dbContext.HomeLabInstallations
            .Include(item => item.ServiceEndpoints)
            .SingleOrDefaultAsync(item => item.Id == installationId, cancellationToken)
            ?? throw new InvalidOperationException("The Home Lab installation was not found.");
        foreach (var endpoint in installation.ServiceEndpoints.Where(item =>
                     item.Scope == (int)HomeLabEndpointScope.Public ||
                     item.Scope == (int)HomeLabEndpointScope.Client && item.EdgeGatewayRouteId.HasValue))
        {
            await RefreshEndpointHealthInternalAsync(installation, endpoint, cancellationToken);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return installation.ServiceEndpoints
            .OrderBy(item => item.Scope)
            .Select(MapServiceEndpoint)
            .ToArray();
    }

    private async Task RefreshEndpointHealthInternalAsync(
        HomeLabInstallationEntity installation,
        HomeLabServiceEndpointEntity endpoint,
        CancellationToken cancellationToken)
    {
        var checks = new List<string>();
        var state = HomeLabEndpointHealthState.Healthy;
        try
        {
            if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) ||
                !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The generated public endpoint is not an HTTPS URL.");
            }
            if (IsPrivateOrLoopbackHost(uri.Host))
            {
                throw new InvalidOperationException("The generated public endpoint exposes a local or private hostname.");
            }
            if (endpoint.EdgeGatewayRouteId is not Guid routeId)
            {
                throw new InvalidOperationException("The public endpoint is not linked to an Edge Gateway route.");
            }

            var routeCheck = await edgeGatewayService.TestRouteAsync(routeId, cancellationToken);
            checks.Add(routeCheck.Summary);
            if (routeCheck.Status == EdgeGatewayDiagnosticStatus.Broken)
            {
                throw new InvalidOperationException(routeCheck.Summary);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
            using var client = new HttpClient(handler);
            using var response = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, uri),
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            var status = (int)response.StatusCode;
            if (status >= 500)
            {
                throw new InvalidOperationException($"The Edge Gateway endpoint returned HTTP {status}.");
            }
            if (response.Headers.Location is { } location)
            {
                var absoluteLocation = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (IsPrivateOrLoopbackHost(absoluteLocation.Host))
                {
                    throw new InvalidOperationException($"The endpoint redirect leaks private host '{absoluteLocation.Host}'.");
                }
                checks.Add($"Redirect is externally valid ({absoluteLocation.GetLeftPart(UriPartial.Authority)})." );
            }
            checks.Add(status is >= 300 and < 400
                ? "Edge Gateway authentication challenge responded."
                : $"Edge Gateway route responded with HTTP {status}.");

            var access = HomeLabEndpointPlanner.ResolveClientAccess(HomeLabCatalog.GetApp(installation.AppId));
            if (access?.Capabilities is { } capabilities)
            {
                var capabilityResult = await ValidateEndpointCapabilitiesAsync(
                    installation,
                    capabilities,
                    cancellationToken);
                checks.AddRange(capabilityResult.Messages);
                if (!capabilityResult.Succeeded)
                {
                    state = capabilityResult.ProbeFailed
                        ? HomeLabEndpointHealthState.Broken
                        : HomeLabEndpointHealthState.Degraded;
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            state = HomeLabEndpointHealthState.Broken;
            checks.Add(exception.Message);
        }

        endpoint.HealthState = (int)state;
        endpoint.HealthDetail = string.Join(" ", checks.Distinct(StringComparer.Ordinal));
        endpoint.CheckedAtUtc = DateTimeOffset.UtcNow;
        endpoint.UpdatedAtUtc = endpoint.CheckedAtUtc.Value;
    }

    private static async Task<EndpointCapabilityValidationResult> ValidateEndpointCapabilitiesAsync(
        HomeLabInstallationEntity installation,
        HomeLabEndpointCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        if (installation.CaddySourcePort is not int localPort)
        {
            return new EndpointCapabilityValidationResult(false, true, ["The service has no local proxy listener for capability probes."]);
        }

        var messages = new List<string>();
        var missingProbe = false;
        try
        {
            if (capabilities.RangeRequests)
            {
                if (string.IsNullOrWhiteSpace(capabilities.RangeRequestProbePath))
                {
                    missingProbe = true;
                    messages.Add("Range forwarding is declared, but the provider has not supplied a safe range probe path.");
                }
                else
                {
                    using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                    using var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        BuildLocalCapabilityUri(localPort, capabilities.RangeRequestProbePath));
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (response.StatusCode != HttpStatusCode.PartialContent ||
                        response.Content.Headers.ContentRange is null)
                    {
                        throw new InvalidOperationException($"The declared range probe returned HTTP {(int)response.StatusCode} without a valid Content-Range response.");
                    }
                    messages.Add("Range request probe passed through the LMS proxy.");
                }
            }

            if (capabilities.WebSockets)
            {
                if (string.IsNullOrWhiteSpace(capabilities.WebSocketProbePath))
                {
                    missingProbe = true;
                    messages.Add("WebSocket forwarding is declared, but the provider has not supplied a safe WebSocket probe path.");
                }
                else
                {
                    using var socket = new ClientWebSocket();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    var target = BuildLocalCapabilityUri(localPort, capabilities.WebSocketProbePath, "ws");
                    await socket.ConnectAsync(target, timeout.Token);
                    messages.Add("WebSocket handshake passed through the LMS proxy.");
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "LMS endpoint health probe", timeout.Token);
                }
            }

            if (capabilities.ServerSentEvents)
            {
                if (string.IsNullOrWhiteSpace(capabilities.ServerSentEventsProbePath))
                {
                    missingProbe = true;
                    messages.Add("Event-stream forwarding is declared, but the provider has not supplied a safe event-stream probe path.");
                }
                else
                {
                    using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                    using var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        BuildLocalCapabilityUri(localPort, capabilities.ServerSentEventsProbePath));
                    request.Headers.Accept.ParseAdd("text/event-stream");
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (!response.IsSuccessStatusCode ||
                        response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                    {
                        throw new InvalidOperationException($"The declared event-stream probe returned HTTP {(int)response.StatusCode} without an event-stream response.");
                    }
                    messages.Add("Event-stream probe passed through the LMS proxy.");
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or WebSocketException or TaskCanceledException or InvalidOperationException)
        {
            messages.Add(exception.Message);
            return new EndpointCapabilityValidationResult(false, true, messages);
        }

        return new EndpointCapabilityValidationResult(!missingProbe, false, messages);
    }

    private static Uri BuildLocalCapabilityUri(int port, string path, string scheme = "http")
    {
        var normalizedPath = path.Trim();
        if (!normalizedPath.StartsWith("/", StringComparison.Ordinal))
        {
            normalizedPath = $"/{normalizedPath}";
        }
        return new Uri($"{scheme}://127.0.0.1:{port}{normalizedPath}", UriKind.Absolute);
    }

    private static bool IsPrivateOrLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!IPAddress.TryParse(host, out var address))
        {
            return false;
        }
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
               (bytes[0] == 10 || bytes[0] == 127 ||
                bytes[0] == 192 && bytes[1] == 168 ||
                bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 169 && bytes[1] == 254);
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

    public async Task<HomeLabOperationResult> ApplyStandardStorageAsync(
        CancellationToken cancellationToken = default)
    {
        var installations = await dbContext.HomeLabInstallations
            .OrderBy(item => item.DisplayName)
            .ToListAsync(cancellationToken);
        if (installations.Count == 0)
        {
            return Success(
                "Standard storage is ready.",
                $"New HomeLab containers will receive {StandardStorageHostPath} at {StandardStorageContainerPath}.",
                [],
                HomeLabHealthState.Healthy);
        }

        var output = new List<string>();
        var processed = new HashSet<Guid>();
        var failed = new List<string>();
        var recreated = 0;
        var stopped = 0;

        foreach (var gateway in installations.Where(item =>
                     item.AppId.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase)))
        {
            var routed = installations.Where(item =>
                item.NetworkMode.Equals($"container:{gateway.ContainerName}", StringComparison.OrdinalIgnoreCase)).ToArray();
            processed.Add(gateway.Id);
            foreach (var installation in routed)
            {
                processed.Add(installation.Id);
            }

            if (ToHealth(gateway.HealthState) == HomeLabHealthState.Stopped)
            {
                stopped += 1 + routed.Length;
                continue;
            }

            var result = await RecreateVpnGatewayAsync(gateway, [], false, cancellationToken);
            output.AddRange(result.Output);
            if (result.Succeeded)
            {
                recreated += 1 + routed.Length;
            }
            else
            {
                failed.Add(gateway.DisplayName);
            }
        }

        foreach (var installation in installations.Where(item => !processed.Contains(item.Id)))
        {
            if (ToHealth(installation.HealthState) == HomeLabHealthState.Stopped)
            {
                stopped++;
                continue;
            }

            var result = await ExecuteAsync(installation.Id, HomeLabLifecycleAction.Recreate, cancellationToken);
            output.AddRange(result.Output);
            if (result.Succeeded)
            {
                recreated++;
            }
            else
            {
                failed.Add(installation.DisplayName);
            }
        }

        if (failed.Count > 0)
        {
            return Failure(
                "Standard storage was only partly applied.",
                $"{recreated} container(s) now use {StandardStorageContainerPath}. Failed: {string.Join(", ", failed)}.",
                output,
                HomeLabHealthState.Degraded);
        }

        return Success(
            "Standard storage applied.",
            $"{recreated} running container(s) now see {StandardStorageHostPath} at {StandardStorageContainerPath} with live host-mount propagation." +
            (stopped > 0 ? $" {stopped} stopped container(s) will receive it when LMS next recreates them." : string.Empty),
            output,
            HomeLabHealthState.Healthy);
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

    public async Task AssignRecipeRunAsync(
        HomeLabRecipeRunAssignment assignment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var recipeId = assignment.RecipeId?.Trim() ?? string.Empty;
        var recipeName = assignment.RecipeName?.Trim() ?? string.Empty;
        var installationIds = assignment.InstallationIds.Distinct().ToArray();
        if (recipeId.Length is < 1 or > 120 || recipeName.Length is < 1 or > 160 ||
            assignment.RecipeRunId == Guid.Empty || installationIds.Length == 0)
        {
            throw new InvalidOperationException("The Home Lab recipe run assignment is incomplete.");
        }

        var installations = await dbContext.HomeLabInstallations
            .Where(item => installationIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        if (installations.Count != installationIds.Length)
        {
            throw new InvalidOperationException("One or more Home Lab recipe installations no longer exist.");
        }

        var deploymentIds = installations.Select(item => item.DeploymentId).Distinct().ToArray();
        var deployments = await dbContext.HomeLabDeployments
            .Where(item => deploymentIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var deployment in deployments)
        {
            deployment.RecipeRunId = assignment.RecipeRunId;
            deployment.PromptRecipeId = recipeId;
            deployment.ConnectivityJson = assignment.Connectivity is null
                ? string.Empty
                : JsonSerializer.Serialize(assignment.Connectivity, JsonOptions);
            deployment.Name = recipeName;
            deployment.UpdatedAtUtc = now;
        }

        foreach (var installation in installations)
        {
            installation.IsRecipeInstallation = true;
            installation.UpdatedAtUtc = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<HomeLabOperationResult> ReconfigureVpnGatewayAsync(
        Guid installationId,
        IReadOnlyDictionary<string, string> configuration,
        IReadOnlyDictionary<string, string> secretConfiguration,
        CancellationToken cancellationToken = default)
    {
        var gateway = await dbContext.HomeLabInstallations
            .SingleOrDefaultAsync(item => item.Id == installationId, cancellationToken)
            ?? throw new InvalidOperationException("The VPN Gateway installation was not found.");
        if (!gateway.AppId.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase))
        {
            return Failure("VPN Gateway reconfiguration is unavailable.", "The selected installation is not a VPN Gateway.", [], HomeLabHealthState.Failed);
        }

        var requestedName = configuration.TryGetValue("gateway-name", out var suppliedName)
            ? suppliedName.Trim()
            : gateway.DisplayName;
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            return Failure("VPN Gateway name was not changed.", "Enter a gateway name.", [], ToHealth(gateway.HealthState));
        }
        if (requestedName.Length > 160)
        {
            return Failure("VPN Gateway name was not changed.", "Gateway names can contain at most 160 characters.", [], ToHealth(gateway.HealthState));
        }
        var otherGatewayNames = await dbContext.HomeLabInstallations
            .Where(item => item.AppId == "vpn-gateway" && item.Id != installationId)
            .Select(item => item.DisplayName)
            .ToListAsync(cancellationToken);
        if (otherGatewayNames.Any(name => name.Equals(requestedName, StringComparison.OrdinalIgnoreCase)))
        {
            return Failure("VPN Gateway name was not changed.", $"A VPN Gateway named '{requestedName}' already exists.", [], ToHealth(gateway.HealthState));
        }

        var requestedSetupMode = configuration.TryGetValue("configuration-mode", out var configuredSetupMode)
            ? configuredSetupMode
            : "Paste provider config";
        var hasReplacementSecret = secretConfiguration.Values.Any(value => !string.IsNullOrWhiteSpace(value));
        if (requestedSetupMode.Equals("Paste provider config", StringComparison.OrdinalIgnoreCase) && !hasReplacementSecret)
        {
            var existingConfiguration = DeserializeDictionary(gateway.ConfigurationJson);
            existingConfiguration["gateway-name"] = requestedName;
            gateway.ConfigurationJson = JsonSerializer.Serialize(existingConfiguration, JsonOptions);
            gateway.DisplayName = requestedName;
            gateway.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            return Success(
                "VPN Gateway saved.",
                $"This gateway is now named {requestedName}. Its existing provider configuration and routed apps were not changed.",
                [],
                ToHealth(gateway.HealthState));
        }

        var app = HomeLabCatalog.GetApp(gateway.AppId);
        PreparedConfiguration prepared;
        try
        {
            prepared = await PrepareConfigurationAsync(app, configuration, secretConfiguration, false, cancellationToken);
        }
        catch (Exception exception)
        {
            return Failure("VPN Gateway configuration was rejected.", exception.Message, [], ToHealth(gateway.HealthState));
        }

        var previousConfiguration = gateway.ConfigurationJson;
        var previousSecrets = gateway.SecretConfigurationJson;
        var previousDisplayName = gateway.DisplayName;
        gateway.ConfigurationJson = JsonSerializer.Serialize(prepared.Configuration, JsonOptions);
        gateway.SecretConfigurationJson = JsonSerializer.Serialize(prepared.SecretReferences, JsonOptions);
        if (prepared.Configuration.TryGetValue("gateway-name", out var gatewayName) && !string.IsNullOrWhiteSpace(gatewayName))
        {
            gateway.DisplayName = gatewayName.Trim();
        }

        var result = await RecreateVpnGatewayAsync(gateway, [], true, cancellationToken);
        if (!result.Succeeded)
        {
            gateway.ConfigurationJson = previousConfiguration;
            gateway.SecretConfigurationJson = previousSecrets;
            gateway.DisplayName = previousDisplayName;
            await dbContext.SaveChangesAsync(cancellationToken);
            foreach (var reference in prepared.SecretReferences.Values)
            {
                await secretStore.DeleteSecretAsync(reference, cancellationToken);
            }
            return result;
        }

        foreach (var reference in DeserializeDictionary(previousSecrets).Values)
        {
            await secretStore.DeleteSecretAsync(reference, cancellationToken);
        }
        return result with
        {
            Summary = "VPN Gateway reconfigured.",
            Detail = "The new provider profile was applied and every routed app was reconnected to the same gateway namespace."
        };
    }

    public async Task<HomeLabOperationResult> SetNetworkRouteAsync(
        Guid installationId,
        bool useVpnGateway,
        Guid? gatewayInstallationId = null,
        CancellationToken cancellationToken = default)
    {
        var installation = await dbContext.HomeLabInstallations
            .Include(item => item.ServiceEndpoints)
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

        if (app.RequiresVpnGateway && !useVpnGateway)
        {
            return Failure(
                "Direct route is blocked.",
                $"{app.Name} must remain behind VPN Gateway (Gluetun).",
                [],
                HomeLabHealthState.Blocked);
        }

        var output = new List<string>();
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
            var gatewayPreparation = await EnsureVpnGatewayNamespacePortAsync(selectedGateway, app, output, true, cancellationToken);
            if (!gatewayPreparation.Succeeded)
            {
                return gatewayPreparation;
            }
            newNetworkMode = $"container:{selectedGateway.ContainerName}";
        }

        if (installation.NetworkMode.Equals(newNetworkMode, StringComparison.OrdinalIgnoreCase))
        {
            PersistNetworkRouteConfiguration(installation, useVpnGateway, selectedGateway?.Id);
            await RefreshHealthInternalAsync(installation, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Success(
                "Network route already configured.",
                useVpnGateway
                    ? $"{app.Name} is already routed through {selectedGateway!.DisplayName}."
                    : $"{app.Name} is already using a direct route without VPN.",
                [],
                ToHealth(installation.HealthState));
        }

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
                ? $"{app.Name} now uses {selectedGateway!.DisplayName}. It will remain blocked if that gateway is unavailable.{QbittorrentRestartLoginNote(app)}"
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
            var publicIpProbe = await ResolvePublicIpAsync(installation.ContainerName, cancellationToken);
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
                publicIpProbe.PublicIp,
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
        var gatewayContainerId = gatewayInspect["Id"]?.GetValue<string>();
        if (!HomeLabDockerNetworkMode.UsesContainerNamespace(
                actualNetworkMode,
                gatewayContainer,
                gatewayContainerId))
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

        var publicIpProbeFromGateway = await ResolvePublicIpAsync(gatewayContainer, cancellationToken);
        if (string.IsNullOrWhiteSpace(publicIpProbeFromGateway.PublicIp))
        {
            return new HomeLabNetworkSecurity(
                "UNVERIFIED",
                true,
                false,
                $"VPN Gateway (Gluetun): {gatewayContainer}",
                gatewayContainer,
                gatewayHealth.Item1.ToString(),
                null,
                $"Docker confirms the app shares Gluetun's network namespace, but the public IP probe failed: {publicIpProbeFromGateway.Detail}",
                checkedAtUtc);
        }

        var portForwarding = app.Id.Equals("qbittorrent", StringComparison.OrdinalIgnoreCase)
            ? await ResolveQbittorrentPortForwardingAsync(gatewayContainer, installation.ContainerName, gatewayInspect, cancellationToken)
            : (Status: (string?)null, Port: (int?)null, Detail: (string?)null);
        var status = portForwarding.Status is "ACTIVE" ? "SECURED" :
            portForwarding.Status is null ? "SECURED" : "SECURED / FIREWALLED";
        var securedDetail = portForwarding.Detail is null
            ? "Docker network mode, Gluetun health, and the gateway egress IP were verified."
            : $"Docker network mode, Gluetun health, and the gateway egress IP were verified. {portForwarding.Detail}";

        return new HomeLabNetworkSecurity(
            status,
            true,
            true,
            $"VPN Gateway (Gluetun): {gatewayContainer}",
            gatewayContainer,
            gatewayHealth.Item1.ToString(),
            publicIpProbeFromGateway.PublicIp,
            securedDetail,
            checkedAtUtc,
            portForwarding.Status,
            portForwarding.Port,
            portForwarding.Detail);
    }

    private async Task<(string? Status, int? Port, string? Detail)> ResolveQbittorrentPortForwardingAsync(
        string gatewayContainer,
        string qbittorrentContainer,
        JsonObject gatewayInspect,
        CancellationToken cancellationToken)
    {
        var environment = gatewayInspect["Config"]?["Env"]?.AsArray()
            .Select(item => item?.GetValue<string>() ?? string.Empty)
            .ToArray() ?? [];
        var enabled = environment.Any(item => item.Equals("VPN_PORT_FORWARDING=on", StringComparison.OrdinalIgnoreCase));
        if (!enabled)
        {
            return ("OFF", null, "Incoming VPN port forwarding is off. qBittorrent remains private behind Gluetun, but it is firewalled from incoming peers and performance can suffer.");
        }

        var forwardedPortResult = await RunDockerAsync(
            ["exec", gatewayContainer, "cat", "/tmp/gluetun/forwarded_port"],
            $"Read the active VPN forwarded port from {gatewayContainer}",
            cancellationToken);
        if (!int.TryParse(forwardedPortResult.StandardOutput.Trim(), out var forwardedPort) || forwardedPort is < 1 or > 65535)
        {
            var logs = await RunDockerAsync(
                ["logs", "--tail", "200", gatewayContainer],
                $"Read VPN port forwarding status from {gatewayContainer}",
                cancellationToken);
            var forwardingError = ($"{logs.StandardOutput}\n{logs.StandardError}")
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(line => line.Contains("port forwarding", StringComparison.OrdinalIgnoreCase) &&
                                       line.Contains("ERROR", StringComparison.OrdinalIgnoreCase));
            return (
                "UNAVAILABLE",
                null,
                forwardingError is null
                    ? "Gluetun has not obtained an incoming VPN port. Check that the provider profile and selected server support P2P port forwarding."
                    : $"Gluetun could not obtain an incoming VPN port: {forwardingError[(forwardingError.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) + 5)..].Trim()}");
        }

        var preferences = await ReadQbittorrentPreferencesAsync(qbittorrentContainer, cancellationToken);
        if (preferences.Result.ExitCode != 0 || preferences.Settings is not { } settings)
        {
            return ("UNVERIFIED", forwardedPort, $"Gluetun opened VPN port {forwardedPort}, but LMS could not read qBittorrent's listening settings.");
        }

        if (!HomeLabQbittorrentPortForwarding.Matches(settings, forwardedPort))
        {
            var synchronization = await RunDockerAsync(
                [
                    "exec", qbittorrentContainer,
                    "curl", "-fsS",
                    "--retry", "10",
                    "--retry-connrefused",
                    "--retry-delay", "1",
                    "--request", "POST",
                    "--data-urlencode", HomeLabQbittorrentPortForwarding.BuildPreferencesFormValue(forwardedPort),
                    $"http://127.0.0.1:{HomeLabVpnPortForwardingPlan.QbittorrentVpnWebUiPort}/api/v2/app/setPreferences"
                ],
                $"Synchronize qBittorrent with VPN forwarded port {forwardedPort}",
                cancellationToken);
            if (synchronization.ExitCode != 0)
            {
                return ("MISMATCH", forwardedPort, $"Gluetun opened VPN port {forwardedPort}, but qBittorrent could not be synchronized: {NormalizeFailure(synchronization)}");
            }

            preferences = await ReadQbittorrentPreferencesAsync(qbittorrentContainer, cancellationToken);
            if (preferences.Settings is not { } synchronizedSettings ||
                !HomeLabQbittorrentPortForwarding.Matches(synchronizedSettings, forwardedPort))
            {
                return ("MISMATCH", forwardedPort, $"Gluetun opened VPN port {forwardedPort}, but qBittorrent did not retain the matching tun0 listening settings.");
            }

            return ("ACTIVE", forwardedPort, $"LMS synchronized qBittorrent with Gluetun's forwarded VPN port {forwardedPort} through tun0.");
        }

        return ("ACTIVE", forwardedPort, $"Gluetun forwarded VPN port {forwardedPort} and qBittorrent is listening on the same port through tun0.");
    }

    private async Task<(LinuxCommandResult Result, HomeLabQbittorrentPortSettings? Settings)> ReadQbittorrentPreferencesAsync(
        string qbittorrentContainer,
        CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync(
            ["exec", qbittorrentContainer, "curl", "-fsS", $"http://127.0.0.1:{HomeLabVpnPortForwardingPlan.QbittorrentVpnWebUiPort}/api/v2/app/preferences"],
            $"Verify qBittorrent listening port in {qbittorrentContainer}",
            cancellationToken);
        return result.ExitCode == 0 &&
               HomeLabQbittorrentPortForwarding.TryReadSettings(result.StandardOutput, out var settings)
            ? (result, settings)
            : (result, null);
    }

    public async Task<HomeLabOperationResult> UpdateContainerSettingsAsync(
        Guid installationId,
        HomeLabContainerSettingsUpdate settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var installation = await dbContext.HomeLabInstallations
            .SingleOrDefaultAsync(item => item.Id == installationId, cancellationToken)
            ?? throw new InvalidOperationException("The Home Lab installation was not found.");
        var app = HomeLabCatalog.GetApp(installation.AppId);
        var image = NormalizeContainerImage(settings.Image);
        var environment = NormalizeContainerEnvironment(settings.Environment);
        var volumes = NormalizeContainerVolumes(settings.Volumes);

        var protectedEnvironmentKeys = app.Ports
            .Select(port => port.VpnEnvironmentVariable)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Cast<string>()
            .Append(app.PublicUrlEnvironmentVariable ?? string.Empty)
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var secretEnvironmentKeys = DeserializeDictionary(installation.SecretConfigurationJson).Keys
            .Where(key => !key.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invalidKey = environment.Keys.FirstOrDefault(key =>
            protectedEnvironmentKeys.Contains(key) || secretEnvironmentKeys.Contains(key));
        if (invalidKey is not null)
        {
            throw new InvalidOperationException($"'{invalidKey}' is managed by LMS and cannot be changed in the container environment editor.");
        }

        var existingConfigurationJson = installation.ConfigurationJson;
        var persistedConfiguration = DeserializeDictionary(existingConfigurationJson)
            .Where(item => IsInternalConfigurationKey(item.Key))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        if (!persistedConfiguration.ContainsKey(ContainerSettingsBackupImageKey))
        {
            persistedConfiguration[ContainerSettingsBackupImageKey] = installation.Image;
            persistedConfiguration[ContainerSettingsBackupConfigurationKey] = existingConfigurationJson;
            persistedConfiguration[ContainerSettingsBackupVolumesKey] = installation.VolumeMappingsJson;
        }
        foreach (var item in environment)
        {
            persistedConfiguration[item.Key] = item.Value;
        }

        installation.Image = image;
        installation.ConfigurationJson = JsonSerializer.Serialize(persistedConfiguration, JsonOptions);
        installation.VolumeMappingsJson = JsonSerializer.Serialize(
            EnsureStandardStorageBinding(volumes.Select(volume =>
                new HomeLabVolumeBinding(volume.HostPath, volume.ContainerPath, volume.ReadOnly))),
            JsonOptions);
        installation.HealthState = (int)HomeLabHealthState.Starting;
        installation.HealthDetail = "Applying edited container settings.";
        installation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return await ExecuteAsync(installation.Id, HomeLabLifecycleAction.Recreate, cancellationToken);
    }

    public async Task<HomeLabOperationResult> RestoreContainerSettingsAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var installation = await dbContext.HomeLabInstallations
            .Include(item => item.ServiceEndpoints)
            .SingleOrDefaultAsync(item => item.Id == installationId, cancellationToken)
            ?? throw new InvalidOperationException("The Home Lab installation was not found.");
        var configuration = DeserializeDictionary(installation.ConfigurationJson);
        if (!configuration.TryGetValue(ContainerSettingsBackupImageKey, out var image) ||
            !configuration.TryGetValue(ContainerSettingsBackupConfigurationKey, out var configurationJson) ||
            !configuration.TryGetValue(ContainerSettingsBackupVolumesKey, out var volumesJson))
        {
            throw new InvalidOperationException("This container does not have an edited-settings snapshot to restore.");
        }

        _ = NormalizeContainerImage(image);
        _ = DeserializeDictionary(configurationJson);
        _ = DeserializeBindings(volumesJson);
        installation.Image = image;
        installation.ConfigurationJson = configurationJson;
        installation.VolumeMappingsJson = volumesJson;
        installation.HealthState = (int)HomeLabHealthState.Starting;
        installation.HealthDetail = "Restoring the container settings used before manual editing.";
        installation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return await ExecuteAsync(installation.Id, HomeLabLifecycleAction.Recreate, cancellationToken);
    }

    public async Task<HomeLabOperationResult> ExecuteAsync(
        Guid installationId,
        HomeLabLifecycleAction action,
        CancellationToken cancellationToken = default)
    {
        var installation = await dbContext.HomeLabInstallations
            .Include(item => item.ServiceEndpoints)
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
            var routeIds = installation.ServiceEndpoints
                .Where(endpoint => endpoint.EdgeGatewayRouteId.HasValue)
                .Select(endpoint => endpoint.EdgeGatewayRouteId!.Value)
                .Append(installation.EdgeGatewayRouteId ?? Guid.Empty)
                .Where(routeId => routeId != Guid.Empty)
                .Distinct()
                .ToArray();
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
            foreach (var routeId in routeIds)
            {
                try
                {
                    await edgeGatewayRouteRegistrationService.UnregisterClientRouteAsync(routeId, cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    await edgeGatewayService.DeleteRouteAsync(routeId, cancellationToken);
                }
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

        if (action is HomeLabLifecycleAction.Update or HomeLabLifecycleAction.Repair or HomeLabLifecycleAction.Recreate)
        {
            var isRepair = action == HomeLabLifecycleAction.Repair;
            var isRecreate = action == HomeLabLifecycleAction.Recreate;
            var reuseExistingImage = isRepair || isRecreate;
            var operationName = isRepair ? "Repair" : isRecreate ? "Recreate" : "Replace";
            if (isRepair && !app.Id.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase))
            {
                await RefreshHealthInternalAsync(installation, cancellationToken);
                if (ToHealth(installation.HealthState) is not (
                    HomeLabHealthState.Degraded or
                    HomeLabHealthState.Failed or
                    HomeLabHealthState.Blocked))
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                    return Success(
                        "Home Lab repair not needed.",
                        $"{app.Name} is {ToHealth(installation.HealthState).ToString().ToLowerInvariant()}.",
                        output,
                        ToHealth(installation.HealthState));
                }
            }

            if (app.Id.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase))
            {
                return await RecreateVpnGatewayAsync(installation, output, !reuseExistingImage, cancellationToken);
            }

            if (installation.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase))
            {
                var gatewayContainerName = installation.NetworkMode["container:".Length..];
                var gateway = await dbContext.HomeLabInstallations
                    .SingleOrDefaultAsync(item => item.ContainerName == gatewayContainerName, cancellationToken);
                if (gateway is null)
                {
                    return Failure(
                        "Home Lab app update blocked.",
                        "The selected VPN Gateway installation no longer exists.",
                        output,
                        HomeLabHealthState.Blocked);
                }

                if (reuseExistingImage && ToHealth(installation.HealthState) == HomeLabHealthState.Blocked)
                {
                    await RefreshHealthInternalAsync(gateway, cancellationToken);
                    var gatewayHealth = ToHealth(gateway.HealthState);
                    if (gatewayHealth is HomeLabHealthState.Degraded or HomeLabHealthState.Failed)
                    {
                        return await RecreateVpnGatewayAsync(gateway, output, false, cancellationToken);
                    }

                    if (gatewayHealth is HomeLabHealthState.Stopped or HomeLabHealthState.Starting)
                    {
                        await dbContext.SaveChangesAsync(cancellationToken);
                        return Failure(
                            "Home Lab app repair paused.",
                            $"The selected VPN Gateway is {gatewayHealth.ToString().ToLowerInvariant()}. Start it or wait for it to finish starting before repairing this app.",
                            output,
                            HomeLabHealthState.Blocked);
                    }
                }

                var gatewayPreparation = await EnsureVpnGatewayNamespacePortAsync(
                    gateway,
                    app,
                    output,
                    !reuseExistingImage,
                    cancellationToken);
                if (!gatewayPreparation.Succeeded)
                {
                    return gatewayPreparation;
                }
            }

            var targetImage = reuseExistingImage ? installation.Image : $"{app.ImageRepository}:{app.ImageTag}";
            if (!reuseExistingImage)
            {
                var pull = await RunDockerAsync(
                    ["pull", targetImage],
                    $"Pull updated Home Lab image {targetImage}",
                    cancellationToken);
                AppendOutput(output, pull);
                if (pull.ExitCode != 0)
                {
                    return Failure("Home Lab image update failed.", NormalizeFailure(pull), output, HomeLabHealthState.Failed);
                }
            }

            var remove = await RunDockerAsync(
                ["rm", "--force", installation.ContainerName],
                $"{operationName} Home Lab container {installation.ContainerName}",
                cancellationToken);
            AppendOutput(output, remove);
            if (remove.ExitCode != 0 && !ContainsNoSuchContainer(remove))
            {
                return Failure(
                    reuseExistingImage ? "Home Lab container repair failed." : "Home Lab container replacement failed.",
                    NormalizeFailure(remove),
                    output,
                    HomeLabHealthState.Failed);
            }

            installation.Image = targetImage;
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
            var repairedHealth = ToHealth(installation.HealthState);
            if (reuseExistingImage && repairedHealth is not (HomeLabHealthState.Healthy or HomeLabHealthState.Starting))
            {
                return Failure(
                    "Home Lab repair completed, but the container is still faulty.",
                    installation.HealthDetail,
                    output,
                    repairedHealth);
            }

            return Success(
                isRepair ? "Home Lab app repaired." : isRecreate ? "Home Lab app recreated." : "Home Lab app updated.",
                isRepair
                    ? $"{app.Name} was recreated from its saved configuration and existing image. {installation.HealthDetail}"
                    : isRecreate
                        ? $"{app.Name} was recreated from its saved configuration and existing image. LMS reapplied its managed networking and access route. {installation.HealthDetail}"
                        : $"{app.Name} was recreated from {targetImage}.",
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
            if (action is HomeLabLifecycleAction.Start or HomeLabLifecycleAction.Restart &&
                app.Id.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await RefreshWireGuardProfileAsync(installation, cancellationToken);
                }
                catch (InvalidOperationException exception)
                {
                    installation.HealthState = (int)HomeLabHealthState.Failed;
                    installation.HealthDetail = exception.Message;
                    installation.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    return Failure(
                        "VPN Gateway profile refresh failed.",
                        exception.Message,
                        output,
                        HomeLabHealthState.Failed);
                }
            }

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

    private async Task<HomeLabOperationResult> RecreateVpnGatewayAsync(
        HomeLabInstallationEntity gateway,
        List<string> output,
        bool pullImage,
        CancellationToken cancellationToken)
    {
        var gatewayConfiguration = DeserializeDictionary(gateway.ConfigurationJson);
        if (HomeLabVpnPortForwardingPlan.RefreshManagedCallbackCommands(gatewayConfiguration))
        {
            gateway.ConfigurationJson = JsonSerializer.Serialize(gatewayConfiguration, JsonOptions);
            gateway.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var routedInstallations = await dbContext.HomeLabInstallations
            .Where(item => item.NetworkMode == $"container:{gateway.ContainerName}")
            .ToListAsync(cancellationToken);

        if (pullImage)
        {
            var pull = await RunDockerAsync(
                ["pull", gateway.Image],
                $"Pull updated Home Lab VPN Gateway image {gateway.Image}",
                cancellationToken);
            AppendOutput(output, pull);
            if (pull.ExitCode != 0)
            {
                return Failure("VPN Gateway update failed.", NormalizeFailure(pull), output, HomeLabHealthState.Failed);
            }
        }

        var operationName = pullImage ? "update" : "repair";

        foreach (var routed in routedInstallations)
        {
            var removeRouted = await RunDockerAsync(
                ["rm", "--force", routed.ContainerName],
                $"Pause routed Home Lab app {routed.ContainerName} for VPN Gateway {operationName}",
                cancellationToken);
            AppendOutput(output, removeRouted);
            if (removeRouted.ExitCode != 0 && !ContainsNoSuchContainer(removeRouted))
            {
                MarkVpnDependentsBlocked(routedInstallations, $"VPN Gateway {operationName} could not pause all routed apps.");
                await dbContext.SaveChangesAsync(cancellationToken);
                return Failure($"VPN Gateway {operationName} could not pause routed apps.", NormalizeFailure(removeRouted), output, HomeLabHealthState.Failed);
            }
        }

        var removeGateway = await RunDockerAsync(
            ["rm", "--force", gateway.ContainerName],
            $"Replace Home Lab VPN Gateway container {gateway.ContainerName}",
            cancellationToken);
        AppendOutput(output, removeGateway);
        if (removeGateway.ExitCode != 0 && !ContainsNoSuchContainer(removeGateway))
        {
            MarkVpnDependentsBlocked(routedInstallations, $"VPN Gateway {operationName} could not replace the gateway container.");
            await dbContext.SaveChangesAsync(cancellationToken);
            return Failure("VPN Gateway container replacement failed.", NormalizeFailure(removeGateway), output, HomeLabHealthState.Failed);
        }

        var gatewayRun = await RunContainerAsync(
            gateway,
            HomeLabCatalog.GetApp(gateway.AppId),
            DeserializeBindings(gateway.VolumeMappingsJson),
            gatewayConfiguration,
            DeserializeDictionary(gateway.SecretConfigurationJson),
            output,
            cancellationToken);
        if (!gatewayRun.Succeeded)
        {
            gateway.HealthState = (int)HomeLabHealthState.Failed;
            gateway.HealthDetail = $"VPN Gateway {operationName} failed; routed apps remain blocked.";
            MarkVpnDependentsBlocked(routedInstallations, gateway.HealthDetail);
            await dbContext.SaveChangesAsync(cancellationToken);
            return gatewayRun.Result!;
        }

        gateway.PortMappingsJson = JsonSerializer.Serialize(gatewayRun.PortBindings, JsonOptions);
        gateway.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await UpdateCaddyAccessAsync(
            gateway,
            HomeLabCatalog.GetApp(gateway.AppId),
            gatewayRun.PortBindings,
            cancellationToken);
        await RefreshHealthInternalAsync(gateway, cancellationToken);
        var recreatedGatewayHealth = ToHealth(gateway.HealthState);
        if (recreatedGatewayHealth is not (HomeLabHealthState.Healthy or HomeLabHealthState.Starting))
        {
            MarkVpnDependentsBlocked(routedInstallations, $"VPN Gateway {operationName} did not restore a working gateway.");
            await dbContext.SaveChangesAsync(cancellationToken);
            return Failure(
                $"VPN Gateway {operationName} completed, but the gateway is still faulty.",
                gateway.HealthDetail,
                output,
                recreatedGatewayHealth);
        }

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
                routed.HealthDetail = $"Blocked: VPN Gateway {operationName} completed, but this app could not be recreated.";
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
                $"VPN Gateway {operationName} completed with blocked routed apps.",
                $"The gateway was recreated, but these apps need attention: {string.Join(", ", failedRoutedApps)}.",
                output,
                HomeLabHealthState.Degraded);
        }

        return Success(
            pullImage ? "VPN Gateway updated." : "VPN Gateway repaired.",
            routedInstallations.Count == 0
                ? $"{gateway.DisplayName} was recreated from its saved configuration and existing image. {gateway.HealthDetail}"
                : $"{gateway.DisplayName} was recreated and {routedInstallations.Count} routed app(s) were reconnected. {gateway.HealthDetail}",
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

    public async Task<HomeLabConnectionGuide> GetConnectionGuideAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var installations = await dbContext.HomeLabInstallations
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var sourceEntity = installations.SingleOrDefault(item => item.Id == installationId)
            ?? throw new InvalidOperationException("The Home Lab installation was not found.");
        var deployments = await dbContext.HomeLabDeployments
            .AsNoTracking()
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var sourceDeployment = deployments.GetValueOrDefault(sourceEntity.DeploymentId);
        var source = MapInstallation(sourceEntity);
        var candidates = installations
            .Where(item => item.Id != sourceEntity.Id &&
                           (AreConnectionPeers(sourceEntity, item) ||
                            sourceDeployment?.RecipeRunId is Guid recipeRunId &&
                            deployments.TryGetValue(item.DeploymentId, out var deployment) &&
                            deployment.RecipeRunId == recipeRunId))
            .Select(MapInstallation)
            .ToArray();

        return new HomeLabConnectionGuide(
            source.Id,
            source.DisplayName,
            HomeLabConnectionAddressPlanner.Build(source, candidates));
    }

    public async Task<HomeLabOperationResult> PublishExternalAccessAsync(
        Guid installationId,
        HomeLabEdgeGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var installation = await dbContext.HomeLabInstallations
            .Include(item => item.ServiceEndpoints)
            .SingleOrDefaultAsync(item => item.Id == installationId, cancellationToken)
            ?? throw new InvalidOperationException("The Home Lab installation was not found.");
        var app = HomeLabCatalog.GetApp(installation.AppId);
        var sourceDeployment = await dbContext.HomeLabDeployments
            .AsNoTracking()
            .SingleAsync(item => item.Id == installation.DeploymentId, cancellationToken);
        var connectivity = DeserializePublishedConnectivity(sourceDeployment.ConnectivityJson);
        var requestedClientAccess = ResolveRecipeClientAccess(app, connectivity);
        if (!app.EdgeGatewaySupported || requestedClientAccess is null)
        {
            return Failure(
                "External access is unavailable.",
                $"{app.Name} is not declared as a public client service in this recipe.",
                [],
                ToHealth(installation.HealthState));
        }

        var deploymentIds = sourceDeployment.RecipeRunId is Guid recipeRunId
            ? await dbContext.HomeLabDeployments
                .AsNoTracking()
                .Where(item => item.RecipeRunId == recipeRunId)
                .Select(item => item.Id)
                .ToArrayAsync(cancellationToken)
            : [installation.DeploymentId];
        var candidates = await dbContext.HomeLabInstallations
            .Include(item => item.ServiceEndpoints)
            .Where(item => deploymentIds.Contains(item.DeploymentId))
            .OrderBy(item => item.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var exposed = candidates
            .Select(item => (Installation: item, App: HomeLabCatalog.GetApp(item.AppId)))
            .Select(item => (item.Installation, item.App, Access: ResolveRecipeClientAccess(item.App, connectivity)))
            .Where(item => item.App.EdgeGatewaySupported && item.Access is not null)
            .ToArray();
        var registeredRouteIds = new List<Guid>();
        var output = new List<string>();
        try
        {
            foreach (var item in exposed)
            {
                var access = item.Access!;
                var proxy = access.ReverseProxy ?? new HomeLabReverseProxyManifest();
                _ = DeserializePortBindings(item.Installation.PortMappingsJson)
                    .FirstOrDefault(binding => binding.Name.Equals(access.PortName, StringComparison.OrdinalIgnoreCase) && binding.HostPort > 0)
                    ?? throw new InvalidOperationException($"{item.App.Name} does not have an LMS host listener for '{access.PortName}'.");
                var proxyPort = item.Installation.CaddySourcePort
                    ?? throw new InvalidOperationException($"{item.App.Name} does not have an LMS local access route.");
                var existing = item.Installation.ServiceEndpoints.FirstOrDefault(endpoint =>
                    endpoint.PortName.Equals(access.PortName, StringComparison.OrdinalIgnoreCase) &&
                    endpoint.Scope == (int)HomeLabEndpointScope.Public);
                var endpoint = await edgeGatewayRouteRegistrationService.RegisterClientRouteAsync(
                    new EdgeGatewayClientRouteRegistration(
                        "homelab-deployment",
                        item.Installation.DeploymentId.ToString("D"),
                        item.App.Id,
                        item.Installation.DisplayName,
                        request.Hostname,
                        request.DomainName,
                        "127.0.0.1",
                        proxyPort,
                        exposed.Length == 1 ? "/" : access.PreferredPath,
                        exposed.Length == 1 ? HomeLabClientRoutingStrategy.Subpath : access.Routing,
                        exposed.Length == 1 ? HomeLabBasePathSupportMode.Transparent : proxy.BasePathSupport,
                        proxy.StripPathPrefix,
                        proxy.ForwardPathPrefix,
                        proxy.UsePublicHostHeader,
                        request.AuthMode,
                        existing?.EdgeGatewayRouteId),
                    cancellationToken);
                if (existing?.EdgeGatewayRouteId is null)
                {
                    registeredRouteIds.Add(endpoint.RouteId);
                }
                var now = DateTimeOffset.UtcNow;
                UpsertEndpoint(
                    item.Installation,
                    access.PortName,
                    HomeLabEndpointScope.Public,
                    endpoint.Url,
                    endpoint.Scheme,
                    endpoint.Host,
                    null,
                    endpoint.PathBase,
                    endpoint.RoutingMode,
                    endpoint.RouteId,
                    now);
                UpsertEndpoint(
                    item.Installation,
                    access.PortName,
                    HomeLabEndpointScope.Client,
                    endpoint.Url,
                    endpoint.Scheme,
                    endpoint.Host,
                    null,
                    endpoint.PathBase,
                    endpoint.RoutingMode,
                    endpoint.RouteId,
                    now);
                item.Installation.EdgeGatewayRouteId = endpoint.RouteId;
                item.Installation.UpdatedAtUtc = now;
                output.Add($"{item.Installation.DisplayName}: {endpoint.Url}");
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            foreach (var item in exposed.Where(candidate =>
                         candidate.App.Environment.Values.Any(value => value.Contains("${endpoint:", StringComparison.Ordinal)) ||
                         !string.IsNullOrWhiteSpace(candidate.App.PublicUrlEnvironmentVariable)))
            {
                var recreate = await ExecuteAsync(item.Installation.Id, HomeLabLifecycleAction.Recreate, cancellationToken);
                if (!recreate.Succeeded)
                {
                    throw new InvalidOperationException($"{item.App.Name} could not apply its generated public endpoint: {recreate.Detail}");
                }
                output.Add($"{item.Installation.DisplayName}: applied generated endpoint configuration.");
            }
            return Success(
                "External access enabled.",
                $"Published {exposed.Length} client service(s) using {FormatEdgeGatewayAuthMode(request.AuthMode)}.",
                output,
                ToHealth(installation.HealthState));
        }
        catch (Exception exception)
        {
            foreach (var routeId in registeredRouteIds.AsEnumerable().Reverse())
            {
                try
                {
                    await edgeGatewayRouteRegistrationService.UnregisterClientRouteAsync(routeId, cancellationToken);
                }
                catch (Exception restoreException)
                {
                    logger.LogWarning(restoreException, "Could not remove Edge Gateway route {RouteId} after recipe publication failed.", routeId);
                }
            }

            foreach (var item in exposed)
            {
                var failedPublicEndpoints = item.Installation.ServiceEndpoints
                    .Where(endpoint => endpoint.Scope == (int)HomeLabEndpointScope.Public &&
                                       endpoint.EdgeGatewayRouteId.HasValue &&
                                       registeredRouteIds.Contains(endpoint.EdgeGatewayRouteId.Value))
                    .ToArray();
                dbContext.HomeLabServiceEndpoints.RemoveRange(failedPublicEndpoints);
                foreach (var clientEndpoint in item.Installation.ServiceEndpoints.Where(endpoint =>
                             endpoint.Scope == (int)HomeLabEndpointScope.Client &&
                             endpoint.EdgeGatewayRouteId.HasValue &&
                             registeredRouteIds.Contains(endpoint.EdgeGatewayRouteId.Value)))
                {
                    clientEndpoint.EdgeGatewayRouteId = null;
                }
                if (item.Installation.EdgeGatewayRouteId.HasValue &&
                    registeredRouteIds.Contains(item.Installation.EdgeGatewayRouteId.Value))
                {
                    item.Installation.EdgeGatewayRouteId = null;
                }
                SynchronizeLocalEndpoints(item.Installation);
            }
            await dbContext.SaveChangesAsync(cancellationToken);

            return Failure(
                "External access was not enabled.",
                exception.Message,
                [],
                ToHealth(installation.HealthState));
        }
    }

    public async Task<HomeLabOperationResult> UnpublishExternalAccessAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var installation = await dbContext.HomeLabInstallations
            .Include(item => item.ServiceEndpoints)
            .SingleOrDefaultAsync(item => item.Id == installationId, cancellationToken)
            ?? throw new InvalidOperationException("The Home Lab installation was not found.");
        if (installation.EdgeGatewayRouteId is not Guid routeId)
        {
            return Success(
                "External access is already off.",
                $"{installation.DisplayName} has no LMS-managed Edge Gateway route.",
                [],
                ToHealth(installation.HealthState));
        }

        try
        {
            await edgeGatewayRouteRegistrationService.UnregisterClientRouteAsync(routeId, cancellationToken);
            var publicEndpoints = installation.ServiceEndpoints
                .Where(endpoint => endpoint.Scope == (int)HomeLabEndpointScope.Public)
                .ToArray();
            dbContext.HomeLabServiceEndpoints.RemoveRange(publicEndpoints);
            foreach (var clientEndpoint in installation.ServiceEndpoints.Where(endpoint =>
                         endpoint.Scope == (int)HomeLabEndpointScope.Client &&
                         endpoint.EdgeGatewayRouteId == routeId))
            {
                clientEndpoint.EdgeGatewayRouteId = null;
            }
            installation.EdgeGatewayRouteId = null;
            SynchronizeLocalEndpoints(installation);
            installation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            if (HomeLabCatalog.GetApp(installation.AppId) is { } unpublishedApp &&
                (unpublishedApp.Environment.Values.Any(value => value.Contains("${endpoint:", StringComparison.Ordinal)) ||
                 !string.IsNullOrWhiteSpace(unpublishedApp.PublicUrlEnvironmentVariable)))
            {
                var recreate = await ExecuteAsync(installation.Id, HomeLabLifecycleAction.Recreate, cancellationToken);
                if (!recreate.Succeeded)
                {
                    return Failure(
                        "External access was removed, but the app needs attention.",
                        recreate.Detail,
                        recreate.Output,
                        recreate.HealthState);
                }
            }
            return Success(
                "External access removed.",
                $"{installation.DisplayName} is no longer published through Edge Gateway.",
                [],
                ToHealth(installation.HealthState));
        }
        catch (Exception exception)
        {
            return Failure(
                "External access was not removed.",
                exception.Message,
                [],
                ToHealth(installation.HealthState));
        }
    }

    private async Task RestoreExternalRouteAfterPublishFailureAsync(
        EdgeGatewayRouteEditor? previousEditor,
        Guid savedRouteId,
        CancellationToken cancellationToken)
    {
        if (previousEditor?.Id.HasValue == true)
        {
            await edgeGatewayService.SaveRouteAsync(previousEditor, cancellationToken);
        }
        else
        {
            var failedEditor = await edgeGatewayService.GetEditorAsync(savedRouteId, cancellationToken);
            if (failedEditor.Id.HasValue)
            {
                failedEditor.Enabled = false;
                await edgeGatewayService.SaveRouteAsync(failedEditor, cancellationToken);
            }
        }

        await edgeGatewayService.ApplyCaddyConfigurationAsync(cancellationToken);
    }

    private static string FormatEdgeGatewayAuthMode(EdgeGatewayAuthMode authMode) => authMode switch
    {
        EdgeGatewayAuthMode.PassThrough => "the app's own security",
        EdgeGatewayAuthMode.TemporaryIpApproval => "email IP approval",
        _ => "LMS MFA/passkey protection"
    };

    private static EdgeGatewayRouteEditor CloneEdgeGatewayRouteEditor(EdgeGatewayRouteEditor source) => new()
    {
        Id = source.Id,
        Enabled = source.Enabled,
        DisplayName = source.DisplayName,
        Hostname = source.Hostname,
        DomainName = source.DomainName,
        TargetScheme = source.TargetScheme,
        TargetHost = source.TargetHost,
        TargetPort = source.TargetPort,
        TargetPathPrefix = source.TargetPathPrefix,
        StripPathPrefix = source.StripPathPrefix,
        ForwardPathPrefix = source.ForwardPathPrefix,
        AuthMode = source.AuthMode,
        UsePublicHostHeader = source.UsePublicHostHeader,
        UpstreamSourceAddress = source.UpstreamSourceAddress,
        StripForwardedFor = source.StripForwardedFor,
        SkipUpstreamTlsVerification = source.SkipUpstreamTlsVerification,
        AllowedUsers = source.AllowedUsers,
        AllowedGroups = source.AllowedGroups,
        AllowLanOnly = source.AllowLanOnly,
        AllowKnownIps = source.AllowKnownIps,
        TemporaryIpApprovalRecipients = source.TemporaryIpApprovalRecipients,
        TemporaryIpApprovalAllowedCountryCodes = source.TemporaryIpApprovalAllowedCountryCodes,
        TemporaryIpApprovalUseNotFoundResponse = source.TemporaryIpApprovalUseNotFoundResponse,
        TemporaryIpApprovalIdleTimeoutMinutes = source.TemporaryIpApprovalIdleTimeoutMinutes,
        TemporaryIpApprovalMaxLifetimeMinutes = source.TemporaryIpApprovalMaxLifetimeMinutes,
        Notes = source.Notes
    };

    public async Task<HomeLabApplicationConfigInspection> InspectApplicationConfigAsync(
        Guid installationId,
        string? relativePath = null,
        CancellationToken cancellationToken = default)
    {
        var installation = await dbContext.HomeLabInstallations
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == installationId, cancellationToken)
            ?? throw new InvalidOperationException("The Home Lab installation was not found.");
        var roots = ResolveConfigurationRoots(installation);
        if (roots.Count == 0)
        {
            return new HomeLabApplicationConfigInspection(
                installation.Id,
                installation.DisplayName,
                [],
                "This app has no LMS-declared persistent configuration mount that can be inspected safely.");
        }

        var candidates = string.IsNullOrWhiteSpace(relativePath)
            ? roots.SelectMany(root => Directory.Exists(root.HostPath)
                    ? Directory.EnumerateFiles(root.HostPath, "*", SearchOption.TopDirectoryOnly)
                        .Where(IsInspectableConfigFile)
                        .Select(path => (Root: root, Path: path))
                    : [])
                .OrderBy(item => Path.GetFileName(item.Path).Equals("config.xml", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray()
            : [ResolveConfigurationFile(roots, relativePath)];

        var files = new List<HomeLabApplicationConfigFile>();
        var totalBytes = 0L;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(candidate.Path);
            if (!info.Exists || info.LinkTarget is not null || info.Length > MaximumConfigFileBytes || totalBytes + info.Length > MaximumConfigInspectionBytes)
            {
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(candidate.Path, cancellationToken);
            string content;
            try { content = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException) { continue; }
            totalBytes += bytes.LongLength;
            files.Add(new HomeLabApplicationConfigFile(
                BuildConfigurationRelativePath(candidate.Root, candidate.Path),
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.LongLength,
                info.LastWriteTimeUtc,
                RedactConfiguration(content)));
        }

        return new HomeLabApplicationConfigInspection(
            installation.Id,
            installation.DisplayName,
            files,
            files.Count == 0
                ? "No supported small text configuration files were found. LMS does not expose databases, binary files, arbitrary container paths, or secret values to AI."
                : "Configuration values that look like passwords, API keys, tokens, cookies, credentials, or private keys are redacted.");
    }

    public async Task<HomeLabApplicationConfigRepairResult> RepairApplicationConfigAsync(
        Guid installationId,
        HomeLabApplicationConfigPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (string.IsNullOrWhiteSpace(patch.ExpectedText) || patch.ExpectedText.Length > 4096 || patch.ReplacementText.Length > 4096)
        {
            throw new InvalidOperationException("The configuration repair must replace one exact text value of at most 4096 characters.");
        }
        if (patch.ExpectedText.Contains("<redacted>", StringComparison.OrdinalIgnoreCase) ||
            patch.ReplacementText.Contains("<redacted>", StringComparison.OrdinalIgnoreCase) ||
            patch.ExpectedText.Contains('\0') || patch.ReplacementText.Contains('\0'))
        {
            throw new InvalidOperationException("Redacted values cannot be used in a configuration repair.");
        }

        var installation = await dbContext.HomeLabInstallations
            .SingleOrDefaultAsync(item => item.Id == installationId, cancellationToken)
            ?? throw new InvalidOperationException("The Home Lab installation was not found.");
        var target = ResolveConfigurationFile(ResolveConfigurationRoots(installation), patch.RelativePath);
        var originalBytes = await File.ReadAllBytesAsync(target.Path, cancellationToken);
        if (originalBytes.LongLength > MaximumConfigFileBytes)
        {
            throw new InvalidOperationException("The configuration file is too large for an AI-assisted repair.");
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
        if (!actualHash.Equals(patch.ExpectedSha256?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The configuration file changed after inspection. Inspect it again before proposing a repair.");
        }

        string originalText;
        try { originalText = StrictUtf8.GetString(originalBytes); }
        catch (DecoderFallbackException) { throw new InvalidOperationException("The configuration file is not valid UTF-8 text and cannot be edited safely."); }
        var occurrenceCount = CountOccurrences(originalText, patch.ExpectedText);
        if (occurrenceCount != 1)
        {
            throw new InvalidOperationException($"The expected configuration text must occur exactly once; LMS found {occurrenceCount} matches.");
        }

        var replacement = originalText.Replace(patch.ExpectedText, patch.ReplacementText, StringComparison.Ordinal);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        var backupDirectory = Path.Combine(target.Root.HostPath, ".lms-ai-backups", timestamp);
        var backupPath = Path.Combine(backupDirectory, Path.GetFileName(target.Path));
        var backupRelativePath = BuildConfigurationRelativePath(target.Root, backupPath);
        var tempPath = Path.Combine(Path.GetTempPath(), $"lms-config-repair-{Guid.NewGuid():N}");
        var edited = false;
        var containerStopped = false;
        var rolledBack = false;

        try
        {
            EnsureSuccess(
                await RunAsync(new LinuxCommandRequest("mkdir", ["-p", backupDirectory], true, DockerCommandTimeout, $"Create protected configuration backup directory for {installation.DisplayName}"), cancellationToken),
                "Could not create the configuration backup directory.");
            EnsureSuccess(
                await RunAsync(new LinuxCommandRequest("cp", ["--preserve=all", target.Path, backupPath], true, DockerCommandTimeout, $"Back up {installation.DisplayName} configuration"), cancellationToken),
                "Could not back up the configuration file.");
            EnsureSuccess(
                await RunDockerAsync(["stop", installation.ContainerName], $"Stop only Home Lab app {installation.DisplayName} for configuration repair", cancellationToken),
                "Could not stop the selected app.");
            containerStopped = true;

            await File.WriteAllTextAsync(tempPath, replacement, new UTF8Encoding(false), cancellationToken);
            EnsureSuccess(
                await RunAsync(new LinuxCommandRequest("cp", [tempPath, target.Path], true, DockerCommandTimeout, $"Apply a reviewed configuration repair to {installation.DisplayName}"), cancellationToken),
                "Could not apply the configuration repair.");
            edited = true;
            EnsureSuccess(
                await RunDockerAsync(["start", installation.ContainerName], $"Start only Home Lab app {installation.DisplayName} after configuration repair", cancellationToken),
                "Could not restart the selected app.");
            containerStopped = false;

            var verified = await WaitForApplicationRepairAsync(installation, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (!verified)
            {
                throw new InvalidOperationException("The app did not pass its Docker health and LMS local-access checks after the configuration change.");
            }

            return new HomeLabApplicationConfigRepairResult(
                true,
                false,
                installation.DisplayName,
                patch.RelativePath,
                backupRelativePath,
                "Applied one exact configuration replacement, restarted only this app, and verified Docker health and LMS local access.",
                ToHealth(installation.HealthState),
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (edited || containerStopped)
        {
            rolledBack = edited;
            if (edited) await RunDockerAsync(["stop", installation.ContainerName], $"Stop {installation.DisplayName} before configuration rollback", CancellationToken.None);
            var restoreSucceeded = !edited ||
                (await RunAsync(new LinuxCommandRequest("cp", ["--preserve=all", backupPath, target.Path], true, DockerCommandTimeout, $"Roll back {installation.DisplayName} configuration"), CancellationToken.None)).ExitCode == 0;
            var restart = await RunDockerAsync(["start", installation.ContainerName], $"Restart {installation.DisplayName} after configuration rollback", CancellationToken.None);
            await RefreshHealthInternalAsync(installation, CancellationToken.None);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            if (exception is OperationCanceledException) throw;
            var rollbackDetail = restoreSucceeded && restart.ExitCode == 0
                ? "The original file was restored and only this app was restarted."
                : "LMS attempted the rollback, but the app still needs attention.";
            return new HomeLabApplicationConfigRepairResult(
                false,
                rolledBack,
                installation.DisplayName,
                patch.RelativePath,
                backupRelativePath,
                $"{exception.Message} {rollbackDetail}",
                ToHealth(installation.HealthState),
                DateTimeOffset.UtcNow);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private IReadOnlyList<HomeLabConfigurationRoot> ResolveConfigurationRoots(HomeLabInstallationEntity installation)
    {
        var app = HomeLabCatalog.GetApp(installation.AppId);
        var bindings = DeserializeBindings(installation.VolumeMappingsJson);
        return app.Volumes
            .Where(volume => volume.Kind == HomeLabStorageKind.Configuration)
            .Select(volume => (Volume: volume, Binding: bindings.FirstOrDefault(binding => binding.ContainerPath.Equals(volume.ContainerPath, StringComparison.Ordinal))))
            .Where(item => item.Binding is not null && !item.Binding.ReadOnly)
            .Select(item => new HomeLabConfigurationRoot(item.Volume.Id, Path.GetFullPath(item.Binding!.HostPath)))
            .ToArray();
    }

    private static (HomeLabConfigurationRoot Root, string Path) ResolveConfigurationFile(
        IReadOnlyList<HomeLabConfigurationRoot> roots,
        string relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
        var separator = normalized.IndexOf('/');
        var rootId = separator < 0 ? normalized : normalized[..separator];
        var child = separator < 0 ? string.Empty : normalized[(separator + 1)..];
        var root = roots.FirstOrDefault(item => item.Id.Equals(rootId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The requested file is outside this app's LMS-declared configuration mounts.");
        if (string.IsNullOrWhiteSpace(child))
        {
            throw new InvalidOperationException("Specify a file below the configuration mount name.");
        }

        var rootPath = Path.GetFullPath(root.HostPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(rootPath, child.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(rootPath, StringComparison.Ordinal) || !IsInspectableConfigFile(fullPath))
        {
            throw new InvalidOperationException("The requested file is outside this app's safe text configuration scope.");
        }
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.LinkTarget is not null || ContainsSymbolicLinkDirectory(rootPath, fullPath))
        {
            throw new InvalidOperationException("The requested configuration file does not exist or is a symbolic link.");
        }
        return (root, fullPath);
    }

    private async Task<bool> WaitForApplicationRepairAsync(HomeLabInstallationEntity installation, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            await RefreshHealthInternalAsync(installation, cancellationToken);
            if (ToHealth(installation.HealthState) == HomeLabHealthState.Healthy && await ProbeCaddyAccessAsync(installation, cancellationToken))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<bool> ProbeCaddyAccessAsync(HomeLabInstallationEntity installation, CancellationToken cancellationToken)
    {
        if (installation.CaddySourcePort is not int port || port <= 0) return true;
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/");
        request.Headers.Host = $"{Dns.GetHostName()}:{port}";
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var code = (int)response.StatusCode;
            return code < 500 && code != (int)HttpStatusCode.BadRequest;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private static bool IsInspectableConfigFile(string path) =>
        InspectableConfigExtensions.Contains(Path.GetExtension(path)) &&
        !path.Contains($"{Path.DirectorySeparatorChar}.lms-ai-backups{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static bool ContainsSymbolicLinkDirectory(string rootPath, string fullPath)
    {
        var relativeDirectory = Path.GetDirectoryName(Path.GetRelativePath(rootPath, fullPath));
        if (string.IsNullOrWhiteSpace(relativeDirectory) || relativeDirectory == ".") return false;
        var current = rootPath;
        foreach (var segment in relativeDirectory.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (new DirectoryInfo(current).LinkTarget is not null) return true;
        }
        return false;
    }

    private static string BuildConfigurationRelativePath(HomeLabConfigurationRoot root, string path) =>
        $"{root.Id}/{Path.GetRelativePath(root.HostPath, path).Replace(Path.DirectorySeparatorChar, '/')}";

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        for (var index = 0; (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length) count++;
        return count;
    }

    internal static string RedactConfiguration(string content)
    {
        const string secretName = "(?:api[_-]?key|password|passwd|token|secret|credential|authorization|cookie|private[_-]?key)";
        var redacted = Regex.Replace(
            content,
            $@"(<(?<name>[^<>]*{secretName}[^<>]*)[^>]*>).*?(</\k<name>>)",
            "$1<redacted>$3",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        redacted = Regex.Replace(
            redacted,
            $"(?<prefix>\\b(?:key|name)\\s*=\\s*[\"'][^\"']*{secretName}[^\"']*[\"']\\s+\\bvalue\\s*=\\s*[\"'])(?<value>[^\"']*)(?<suffix>[\"'])",
            "${prefix}<redacted>${suffix}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        redacted = Regex.Replace(
            redacted,
            $"(?<prefix>\\b{secretName}\\s*=\\s*[\"'])(?<value>[^\"']*)(?<suffix>[\"'])",
            "${prefix}<redacted>${suffix}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        redacted = Regex.Replace(
            redacted,
            $"(?<prefix>[\"'][^\"']*{secretName}[^\"']*[\"']\\s*:\\s*)(?<value>\"(?:\\\\.|[^\"\\\\])*\"|'[^'\\r\\n]*'|[^,\\r\\n}}\\]]*)",
            "${prefix}<redacted>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(
            redacted,
            $"(?im)^(?<prefix>\\s*[^=:\\r\\n]*{secretName}[^=:\\r\\n]*\\s*[:=]\\s*)(?<value>[^\\r\\n]*)",
            "${prefix}<redacted>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool AreConnectionPeers(
        HomeLabInstallationEntity source,
        HomeLabInstallationEntity target)
    {
        if (source.DeploymentId == target.DeploymentId)
        {
            return true;
        }

        var sourceUsesVpn = source.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase);
        var targetUsesVpn = target.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase);
        if (sourceUsesVpn || targetUsesVpn)
        {
            return sourceUsesVpn && targetUsesVpn &&
                   source.NetworkMode["container:".Length..].TrimStart('/').Equals(
                       target.NetworkMode["container:".Length..].TrimStart('/'),
                       StringComparison.OrdinalIgnoreCase);
        }

        return source.NetworkName.Equals(target.NetworkName, StringComparison.OrdinalIgnoreCase);
    }

    private void SynchronizeLocalEndpoints(HomeLabInstallationEntity installation)
    {
        var app = HomeLabCatalog.GetApp(installation.AppId);
        var bindings = DeserializePortBindings(installation.PortMappingsJson);
        var now = DateTimeOffset.UtcNow;

        if (HomeLabEndpointPlanner.IncludesScope(app, HomeLabEndpointScope.Internal))
        {
            foreach (var port in app.Ports)
            {
                var scheme = ResolveApplicationScheme(port);
                var host = HomeLabEndpointPlanner.ResolveInternalHost(installation);
                var internalPort = HomeLabEndpointPlanner.ResolveInternalPort(port, installation);
                UpsertEndpoint(
                    installation,
                    port.Name,
                    HomeLabEndpointScope.Internal,
                    $"{scheme}://{host}:{internalPort}",
                    scheme,
                    host,
                    internalPort,
                    string.Empty,
                    null,
                    null,
                    now);
            }
        }

        var clientAccess = HomeLabEndpointPlanner.ResolveClientAccess(app);
        var clientPort = clientAccess is null
            ? null
            : bindings.FirstOrDefault(port => port.Name.Equals(clientAccess.PortName, StringComparison.OrdinalIgnoreCase));
        if (clientAccess is null || clientPort is null || installation.CaddySourcePort is not int caddyPort || caddyPort <= 0)
        {
            return;
        }

        var lanHost = Dns.GetHostName();
        var lanUrl = $"http://{lanHost}:{caddyPort}";
        if (HomeLabEndpointPlanner.IncludesScope(app, HomeLabEndpointScope.Lan))
        {
            UpsertEndpoint(
                installation,
                clientAccess.PortName,
                HomeLabEndpointScope.Lan,
                lanUrl,
                "http",
                lanHost,
                caddyPort,
                string.Empty,
                null,
                null,
                now);
        }

        var existingClient = installation.ServiceEndpoints.FirstOrDefault(endpoint =>
            endpoint.PortName.Equals(clientAccess.PortName, StringComparison.OrdinalIgnoreCase) &&
            endpoint.Scope == (int)HomeLabEndpointScope.Client);
        if (existingClient?.EdgeGatewayRouteId is null)
        {
            UpsertEndpoint(
                installation,
                clientAccess.PortName,
                HomeLabEndpointScope.Client,
                lanUrl,
                "http",
                lanHost,
                caddyPort,
                string.Empty,
                null,
                null,
                now);
        }
    }

    private static HomeLabServiceEndpointEntity UpsertEndpoint(
        HomeLabInstallationEntity installation,
        string portName,
        HomeLabEndpointScope scope,
        string url,
        string scheme,
        string host,
        int? port,
        string pathBase,
        HomeLabClientRoutingStrategy? routingMode,
        Guid? edgeGatewayRouteId,
        DateTimeOffset now)
    {
        var endpoint = installation.ServiceEndpoints.FirstOrDefault(item =>
            item.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase) &&
            item.Scope == (int)scope);
        if (endpoint is null)
        {
            endpoint = new HomeLabServiceEndpointEntity
            {
                Id = Guid.NewGuid(),
                InstallationId = installation.Id,
                ServiceId = installation.AppId,
                PortName = portName,
                Scope = (int)scope,
                CreatedAtUtc = now,
                HealthState = (int)HomeLabEndpointHealthState.NotChecked
            };
            installation.ServiceEndpoints.Add(endpoint);
        }

        endpoint.Url = url;
        endpoint.Scheme = scheme;
        endpoint.Host = host;
        endpoint.Port = port;
        endpoint.PathBase = pathBase;
        endpoint.RoutingMode = routingMode.HasValue ? (int)routingMode.Value : null;
        endpoint.EdgeGatewayRouteId = edgeGatewayRouteId;
        endpoint.UpdatedAtUtc = now;
        return endpoint;
    }

    private async Task SynchronizePublishedEndpointAsync(
        HomeLabInstallationEntity installation,
        CancellationToken cancellationToken)
    {
        if (installation.EdgeGatewayRouteId is not Guid routeId)
        {
            return;
        }
        var editor = await edgeGatewayService.GetEditorAsync(routeId, cancellationToken);
        if (editor.Id != routeId || !editor.Enabled || string.IsNullOrWhiteSpace(editor.Hostname))
        {
            return;
        }
        var access = HomeLabEndpointPlanner.ResolveClientAccess(HomeLabCatalog.GetApp(installation.AppId));
        if (access is null)
        {
            return;
        }
        var path = EdgeGatewayRouteValidator.NormalizePathPrefix(editor.TargetPathPrefix);
        var host = editor.Hostname.Trim().TrimEnd('.');
        var url = $"https://{host}{path}";
        var routing = string.IsNullOrWhiteSpace(path)
            ? HomeLabClientRoutingStrategy.Subdomain
            : HomeLabClientRoutingStrategy.Subpath;
        var now = DateTimeOffset.UtcNow;
        UpsertEndpoint(installation, access.PortName, HomeLabEndpointScope.Public, url, "https", host, null, path, routing, routeId, now);
        UpsertEndpoint(installation, access.PortName, HomeLabEndpointScope.Client, url, "https", host, null, path, routing, routeId, now);
    }

    private HomeLabEffectiveContainer BuildEffectiveContainer(
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
            .Concat(BuildVpnPortEnvironment(app, installation.NetworkMode))
            .Concat(BuildPublicUrlEnvironment(app, installation))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}={ResolveEndpointTokens(installation, group.Last().Value)}")
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
                ? "Internet route: VPN Gateway"
                : app.SupportsVpnGateway
                    ? "Internet route: Direct"
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
            environment.Add("WebUI\\ReverseProxySupportEnabled=false");
            environment.Add("WebUI\\HostHeaderValidation=false");
            connections.Add("Web UI access: the local LMS Caddy route is trusted without a separate qBittorrent login");
            connections.Add("Direct application login: use qBittorrent credentials; the initial admin password is in the container logs until changed");
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
            app.Dependencies,
            configuration.ContainsKey(ContainerSettingsBackupImageKey));
    }

    private async Task EnsureCaddyAccessAsync(
        HomeLabInstallationEntity installation,
        CancellationToken cancellationToken)
    {
        var app = HomeLabCatalog.GetApp(installation.AppId);
        var primaryManifestPort = ResolvePrimaryPort(app);
        if (primaryManifestPort is null ||
            !primaryManifestPort.Name.Equals("web", StringComparison.OrdinalIgnoreCase))
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
        var rewriteSecureCookiesForHttp = HomeLabEndpointPlanner.ResolveClientAccess(app)?
            .ReverseProxy?.RewriteSecureCookiesForHttp == true;
        if (installation.CaddyRouteId is Guid routeId)
        {
            var existing = await caddyIntegrationService.GetEditorAsync(routeId, cancellationToken);
            if (existing.Id == routeId)
            {
                if (existing.DestinationIp != "127.0.0.1" ||
                    existing.DestinationPort != primaryPort.HostPort ||
                    existing.RewriteSecureCookiesForHttp != rewriteSecureCookiesForHttp)
                {
                    existing.DestinationIp = "127.0.0.1";
                    existing.DestinationPort = primaryPort.HostPort;
                    existing.RewriteSecureCookiesForHttp = rewriteSecureCookiesForHttp;
                    await caddyIntegrationService.SaveRouteAsync(existing, cancellationToken);
                }
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
                existing.RewriteSecureCookiesForHttp = rewriteSecureCookiesForHttp;
                installation.CaddyRouteId = savedRoute.Id;
                await caddyIntegrationService.SaveRouteAsync(existing, cancellationToken);
                installation.CaddySourcePort = existing.SourcePort;
                return;
            }
        }

        var sourcePort = installation.CaddySourcePort
            ?? await ResolveCaddySourcePortAsync(cancellationToken);
        var editor = new CaddyProxyRouteEditor
        {
            Kind = CaddyProxyRouteKind.PortForward,
            Name = routeName,
            Description = $"Home Lab browser access for {app.Name}. Managed by LMS.",
            SourceIp = "0.0.0.0",
            SourcePort = sourcePort,
            DestinationIp = "127.0.0.1",
            DestinationPort = primaryPort.HostPort,
            DestinationScheme = CaddyProxyTargetScheme.Http,
            RewriteSecureCookiesForHttp = rewriteSecureCookiesForHttp
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
        var primaryManifestPort = ResolvePrimaryPort(app);
        if (primaryManifestPort is null)
        {
            await RemoveCaddyAccessAsync(installation, cancellationToken);
            return;
        }

        var primaryPort = ports.FirstOrDefault(port =>
            port.Name.Equals(primaryManifestPort.Name, StringComparison.OrdinalIgnoreCase));
        if (primaryPort is null || primaryPort.HostPort <= 0)
        {
            await RemoveCaddyAccessAsync(installation, cancellationToken);
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
        editor.RewriteSecureCookiesForHttp = HomeLabEndpointPlanner.ResolveClientAccess(app)?
            .ReverseProxy?.RewriteSecureCookiesForHttp == true;
        await caddyIntegrationService.SaveRouteAsync(editor, cancellationToken);
        installation.CaddySourcePort = editor.SourcePort;
    }

    private async Task RemoveCaddyAccessAsync(
        HomeLabInstallationEntity installation,
        CancellationToken cancellationToken)
    {
        if (installation.CaddyRouteId is Guid routeId)
        {
            await caddyIntegrationService.DeleteRouteAsync(routeId, cancellationToken);
        }

        installation.CaddyRouteId = null;
        installation.CaddySourcePort = null;
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

    private async Task ReserveCaddyAccessPortAsync(
        HomeLabInstallationEntity installation,
        HomeLabAppManifest app,
        CancellationToken cancellationToken)
    {
        var primaryPort = ResolvePrimaryPort(app);
        if (primaryPort?.Name.Equals("web", StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        installation.CaddySourcePort ??= await ResolveCaddySourcePortAsync(cancellationToken);
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
                await ReserveCaddyAccessPortAsync(installation, app, cancellationToken);
                if (installation.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase) &&
                    reusableVpnGateway is not null)
                {
                    var gatewayPreparation = await EnsureVpnGatewayNamespacePortAsync(
                        reusableVpnGateway,
                        app,
                        output,
                        true,
                        cancellationToken);
                    if (!gatewayPreparation.Succeeded)
                    {
                        throw new InvalidOperationException(gatewayPreparation.Detail);
                    }
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
                    throw new InvalidOperationException(run.Result?.Detail ?? "The Home Lab container could not be created.");
                }

                createdContainers.Add(installation.ContainerName);
                installation.PortMappingsJson = JsonSerializer.Serialize(run.PortBindings, JsonOptions);
                await RefreshHealthInternalAsync(installation, cancellationToken);

                if (edgeGateway is not null && app.Id == apps[0].Id)
                {
                    var primaryManifestPort = ResolvePrimaryPort(app);
                    var primaryPort = run.PortBindings.FirstOrDefault(port =>
                        primaryManifestPort is not null &&
                        port.Name.Equals(primaryManifestPort.Name, StringComparison.OrdinalIgnoreCase));
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
                SynchronizeLocalEndpoints(installation);
                await SynchronizePublishedEndpointAsync(installation, cancellationToken);
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
        var bindings = EnsureStandardStorageBinding(app.Volumes.Select(volume => new HomeLabVolumeBinding(
            ResolveVolumeHostPath(deployment.Id, app, volume, storagePaths),
            volume.ContainerPath,
            volume.ReadOnly)));
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
            IsRecipeInstallation = isRecipe,
            Deployment = deployment
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

            var volume = app.Volumes.FirstOrDefault(candidate =>
                candidate.ContainerPath.Equals(binding.ContainerPath, StringComparison.Ordinal));
            if (volume?.HostOwner is { Length: > 0 } hostOwner)
            {
                var chown = await RunAsync(
                    new LinuxCommandRequest(
                        "chown",
                        ["-R", hostOwner, binding.HostPath],
                        true,
                        TimeSpan.FromSeconds(30),
                        $"Set Home Lab volume ownership for {binding.ContainerPath}"),
                    cancellationToken);
                AppendOutput(output, chown);
                if (chown.ExitCode != 0)
                {
                    return ContainerRunResult.Failed(Failure("Home Lab volume ownership setup failed.", NormalizeFailure(chown), output, HomeLabHealthState.Failed));
                }
            }
        }

        await EnsureApplicationConfigurationAsync(installation, app, bindings, cancellationToken);

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
                var fileContent = await PrepareSecretFileContentAsync(app, containerPath, secret, cancellationToken);
                await WriteSecretFileAsync(hostPath, fileContent, cancellationToken);
                continue;
            }
            resolvedSecrets[secretReference.Key] = secret;
        }

        var environmentValues = app.Environment
            .Concat(configuration.Where(item => !IsInternalConfigurationKey(item.Key)))
            .Concat(BuildGatewayEnvironment(app))
            .Concat(BuildVpnPortEnvironment(app, installation.NetworkMode))
            .Concat(BuildPublicUrlEnvironment(app, installation))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .Select(environment => new KeyValuePair<string, string>(
                environment.Key,
                ResolveEndpointTokens(installation, environment.Value)));
        foreach (var environment in environmentValues)
        {
            args.AddRange(["--env", $"{environment.Key}={environment.Value}"]);
        }

        foreach (var binding in bindings)
        {
            if (IsStandardStorageBinding(binding))
            {
                args.AddRange([
                    "--mount",
                    $"type=bind,src={binding.HostPath},dst={binding.ContainerPath},bind-propagation=rslave"
                ]);
            }
            else
            {
                args.AddRange(["--volume", $"{binding.HostPath}:{binding.ContainerPath}{(binding.ReadOnly ? ":ro" : string.Empty)}"]);
            }
        }

        if (installation.NetworkMode.Equals("bridge", StringComparison.OrdinalIgnoreCase))
        {
            var publishedPorts = app.Id.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase)
                ? HomeLabContainerPortPlan.GatewayPublishedPorts()
                : HomeLabEndpointPlanner.IncludesScope(app, HomeLabEndpointScope.Lan) ||
                  HomeLabEndpointPlanner.IncludesScope(app, HomeLabEndpointScope.Client)
                    ? app.Ports.Select(port => (port.Protocol, HomeLabContainerPortPlan.Resolve(port, false)))
                    : [];
            foreach (var port in publishedPorts.Distinct())
            {
                args.AddRange(["--publish", $"127.0.0.1::{port.Item2}/{port.Item1}"]);
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

        var vpnFileOverrideCommand = HomeLabContainerPortPlan.BuildVpnFileOverrideCommand(
            app,
            installation.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase));
        if (vpnFileOverrideCommand is not null)
        {
            args.AddRange(["--entrypoint", "/bin/sh"]);
        }

        args.Add(installation.Image);
        if (vpnFileOverrideCommand is not null)
        {
            args.AddRange(["-c", vpnFileOverrideCommand]);
        }
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
            ? ParsePortBindings(inspect, app, false)
            : await ParseSharedNamespacePortBindingsAsync(installation.NetworkMode, app, cancellationToken);
        var requiresPublishedPorts = HomeLabEndpointPlanner.IncludesScope(app, HomeLabEndpointScope.Lan) ||
                                     HomeLabEndpointPlanner.IncludesScope(app, HomeLabEndpointScope.Client);
        if (installation.NetworkMode.Equals("bridge", StringComparison.OrdinalIgnoreCase) &&
            (ports.Count != app.Ports.Count || requiresPublishedPorts && ports.Any(port => port.HostPort <= 0)))
        {
            return ContainerRunResult.Failed(Failure("Home Lab port inspection failed.", "The container was created but LMS could not resolve all published ports.", output, HomeLabHealthState.Failed));
        }

        return ContainerRunResult.Completed(ports);
    }

    private async Task<bool> EnsureApplicationConfigurationAsync(
        HomeLabInstallationEntity installation,
        HomeLabAppManifest app,
        IReadOnlyList<HomeLabVolumeBinding> bindings,
        CancellationToken cancellationToken)
    {
        var basePathChanged = EnsureReverseProxyBasePathConfiguration(installation, app, bindings);
        if (!app.Id.Equals("qbittorrent", StringComparison.OrdinalIgnoreCase))
        {
            return basePathChanged;
        }

        var configBinding = bindings.FirstOrDefault(binding =>
            binding.ContainerPath.Equals("/config", StringComparison.OrdinalIgnoreCase));
        if (configBinding is null)
        {
            throw new InvalidOperationException("qBittorrent requires a mounted /config volume for LMS proxy compatibility settings.");
        }

        var proxyGatewaySubnet = await ResolveQbittorrentProxyGatewaySubnetAsync(installation, cancellationToken);

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
            WebUI\ReverseProxySupportEnabled=false
            WebUI\HostHeaderValidation=false
            WebUI\LocalHostAuth=false
            WebUI\AuthSubnetWhitelist={proxyGatewaySubnet}
            WebUI\AuthSubnetWhitelistEnabled=true
            Connection\UPnP=false
            LMS_QBITTORRENT_CONFIG
                changed=1
            else
                if ! grep -q '^WebUI\\ReverseProxySupportEnabled=false$' "$config_file"; then
                    if grep -q '^WebUI\\ReverseProxySupportEnabled=' "$config_file"; then
                        sed -i 's/^WebUI\\ReverseProxySupportEnabled=.*/WebUI\\ReverseProxySupportEnabled=false/' "$config_file"
                    elif grep -q '^\\[Preferences\\]$' "$config_file"; then
                        sed -i '/^\\[Preferences\\]$/a WebUI\\ReverseProxySupportEnabled=false' "$config_file"
                    else
                        printf '\n[Preferences]\nWebUI\ReverseProxySupportEnabled=false\n' >> "$config_file"
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
                if ! grep -q '^WebUI\\LocalHostAuth=false$' "$config_file"; then
                    if grep -q '^WebUI\\LocalHostAuth=' "$config_file"; then
                        sed -i 's/^WebUI\\LocalHostAuth=.*/WebUI\\LocalHostAuth=false/' "$config_file"
                    elif grep -q '^\[Preferences\]$' "$config_file"; then
                        sed -i '/^\[Preferences\]$/a WebUI\\LocalHostAuth=false' "$config_file"
                    fi
                    changed=1
                fi
                if ! grep -Fqx 'WebUI\AuthSubnetWhitelist={proxyGatewaySubnet}' "$config_file"; then
                    if grep -q '^WebUI\\AuthSubnetWhitelist=' "$config_file"; then
                        sed -i 's|^WebUI\\AuthSubnetWhitelist=.*|WebUI\\AuthSubnetWhitelist={proxyGatewaySubnet}|' "$config_file"
                    elif grep -q '^\[Preferences\]$' "$config_file"; then
                        sed -i '/^\[Preferences\]$/a WebUI\\AuthSubnetWhitelist={proxyGatewaySubnet}' "$config_file"
                    fi
                    changed=1
                fi
                if ! grep -q '^WebUI\\AuthSubnetWhitelistEnabled=true$' "$config_file"; then
                    if grep -q '^WebUI\\AuthSubnetWhitelistEnabled=' "$config_file"; then
                        sed -i 's/^WebUI\\AuthSubnetWhitelistEnabled=.*/WebUI\\AuthSubnetWhitelistEnabled=true/' "$config_file"
                    elif grep -q '^\[Preferences\]$' "$config_file"; then
                        sed -i '/^\[Preferences\]$/a WebUI\\AuthSubnetWhitelistEnabled=true' "$config_file"
                    fi
                    changed=1
                fi
                if ! grep -q '^Connection\\UPnP=false$' "$config_file"; then
                    if grep -q '^Connection\\UPnP=' "$config_file"; then
                        sed -i 's/^Connection\\UPnP=.*/Connection\\UPnP=false/' "$config_file"
                    elif grep -q '^\[Preferences\]$' "$config_file"; then
                        sed -i '/^\[Preferences\]$/a Connection\\UPnP=false' "$config_file"
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

        return basePathChanged || result.StandardOutput.Trim().EndsWith("1", StringComparison.Ordinal);
    }

    private bool EnsureReverseProxyBasePathConfiguration(
        HomeLabInstallationEntity installation,
        HomeLabAppManifest app,
        IReadOnlyList<HomeLabVolumeBinding> bindings)
    {
        var configuration = HomeLabEndpointPlanner.ResolveClientAccess(app)?.ReverseProxy?.Configuration;
        if (configuration is null)
        {
            return false;
        }

        var value = ResolveEndpointTokens(installation, configuration.ValueTemplate);
        if (!string.IsNullOrWhiteSpace(configuration.EnvironmentVariable))
        {
            var values = DeserializeDictionary(installation.ConfigurationJson);
            if (values.TryGetValue(configuration.EnvironmentVariable, out var existing) &&
                existing.Equals(value, StringComparison.Ordinal))
            {
                return false;
            }
            values[configuration.EnvironmentVariable] = value;
            installation.ConfigurationJson = JsonSerializer.Serialize(values, JsonOptions);
            return true;
        }

        if (string.IsNullOrWhiteSpace(configuration.FilePath) || string.IsNullOrWhiteSpace(configuration.XmlElement))
        {
            throw new InvalidOperationException($"{app.Name} has an incomplete native base-path configuration declaration.");
        }

        var containerPath = configuration.FilePath.Trim();
        var binding = bindings
            .Where(candidate => containerPath.Equals(candidate.ContainerPath, StringComparison.Ordinal) ||
                                containerPath.StartsWith(candidate.ContainerPath.TrimEnd('/') + "/", StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.ContainerPath.Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"{app.Name} base-path configuration is outside its declared volumes.");
        var relative = containerPath[binding.ContainerPath.Length..].TrimStart('/');
        var hostPath = Path.Combine(binding.HostPath, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(hostPath))
        {
            return false;
        }

        var document = XDocument.Load(hostPath, LoadOptions.PreserveWhitespace);
        var element = document.Descendants()
            .FirstOrDefault(candidate => candidate.Name.LocalName.Equals(configuration.XmlElement, StringComparison.OrdinalIgnoreCase));
        if (element?.Value.Equals(value, StringComparison.Ordinal) == true)
        {
            return false;
        }
        if (element is null)
        {
            document.Root?.Add(new XElement(configuration.XmlElement, value));
        }
        else
        {
            element.Value = value;
        }
        document.Save(hostPath, SaveOptions.DisableFormatting);
        return true;
    }

    private async Task<string> ResolveQbittorrentProxyGatewaySubnetAsync(
        HomeLabInstallationEntity installation,
        CancellationToken cancellationToken)
    {
        LinuxCommandResult inspect;
        if (installation.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase))
        {
            var gatewayContainer = installation.NetworkMode["container:".Length..].TrimStart('/');
            inspect = await RunDockerAsync(
                ["inspect", "--format", "{{range .NetworkSettings.Networks}}{{println .Gateway}}{{end}}", gatewayContainer],
                $"Resolve qBittorrent proxy gateway through {gatewayContainer}",
                cancellationToken);
        }
        else
        {
            inspect = await RunDockerAsync(
                ["network", "inspect", "--format", "{{range .IPAM.Config}}{{println .Gateway}}{{end}}", installation.NetworkName],
                $"Resolve qBittorrent proxy gateway on {installation.NetworkName}",
                cancellationToken);
        }

        if (inspect.ExitCode != 0 ||
            !HomeLabQbittorrentWebUiAccess.TryBuildDockerGatewaySubnet(inspect.StandardOutput, out var subnet))
        {
            throw new InvalidOperationException(
                $"qBittorrent's Docker gateway could not be resolved for LMS browser access: {NormalizeFailure(inspect)}");
        }

        return subnet;
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
            : ParsePortBindings(gateway, app, true);
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
        if (inspect is not null &&
            health.Item1 == HomeLabHealthState.Healthy &&
            app.Id.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase))
        {
            var missingPorts = FindMissingVpnGatewayPorts(inspect);
            if (missingPorts.Count > 0)
            {
                health = (
                    HomeLabHealthState.Degraded,
                    $"VPN Gateway is missing required listener ports {string.Join(", ", missingPorts.Select(port => $"{port.Port}/{port.Protocol}"))}. Repair it to reconnect routed apps and rebuild their Caddy routes.");
            }
        }
        if (inspect is not null &&
            health.Item1 == HomeLabHealthState.Healthy &&
            app.HealthCheck?.HttpPath is { Length: > 0 } healthPath)
        {
            health = await ProbeHttpHealthAsync(
                installation,
                app,
                inspect,
                healthPath,
                cancellationToken);
        }
        installation.HealthState = (int)health.Item1;
        installation.HealthDetail = health.Item2;
        installation.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private async Task<(HomeLabHealthState, string)> ProbeHttpHealthAsync(
        HomeLabInstallationEntity installation,
        HomeLabAppManifest app,
        JsonObject inspect,
        string healthPath,
        CancellationToken cancellationToken)
    {
        var healthPort = app.HealthCheck?.Port;
        var manifestPort = app.Ports.FirstOrDefault(port => port.ContainerPort == healthPort)
            ?? ResolvePrimaryPort(app);
        var binding = manifestPort is null
            ? null
            : DeserializePortBindings(installation.PortMappingsJson).FirstOrDefault(port =>
                port.Name.Equals(manifestPort.Name, StringComparison.OrdinalIgnoreCase));
        if (binding is null || binding.HostPort <= 0)
        {
            return (HomeLabHealthState.Degraded, "The container is running, but LMS cannot resolve its HTTP health port.");
        }

        var path = healthPath.StartsWith('/') ? healthPath : $"/{healthPath}";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(app.HealthCheck?.TimeoutSeconds ?? 5));
            using var handler = new SocketsHttpHandler { UseProxy = false };
            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"http://127.0.0.1:{binding.HostPort}{path}");
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if ((int)response.StatusCode < 500)
            {
                return (HomeLabHealthState.Healthy, $"Container is running and HTTP port {binding.ContainerPort} responded.");
            }

            return (HomeLabHealthState.Degraded, $"The container is running, but its HTTP health check returned {(int)response.StatusCode}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            var startedAtText = inspect["State"]?["StartedAt"]?.GetValue<string>();
            var withinStartPeriod = DateTimeOffset.TryParse(startedAtText, out var startedAt) &&
                                    DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(app.HealthCheck?.StartPeriodSeconds ?? 20);
            return withinStartPeriod
                ? (HomeLabHealthState.Starting, "Container is running; waiting for its HTTP endpoint to start.")
                : (HomeLabHealthState.Degraded, $"The container is running, but its HTTP endpoint is unavailable: {exception.Message}");
        }
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

    private async Task<HomeLabPublicIpProbeResult> ResolvePublicIpAsync(
        string containerName,
        CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync(
            [
                "exec",
                containerName,
                "sh",
                "-c",
                "set -u; " +
                "for file in /tmp/gluetun/ip /gluetun/ip; do " +
                "if [ -r \"$file\" ]; then value=$(cat \"$file\" 2>/dev/null | tr -d '[:space:]' || true); " +
                "if [ -n \"$value\" ]; then printf '%s\\n' \"$value\"; exit 0; fi; fi; " +
                "done; " +
                "fetch() { " +
                "if command -v wget >/dev/null 2>&1; then wget -qO- -T 10 \"$1\" 2>/dev/null && return 0; fi; " +
                "if command -v curl >/dev/null 2>&1; then curl -fsS --max-time 10 \"$1\" 2>/dev/null && return 0; fi; " +
                "return 1; }; " +
                "for url in http://127.0.0.1:8000/v1/publicip/ip https://api.ipify.org https://ifconfig.me/ip https://icanhazip.com; do " +
                "response=$(fetch \"$url\" || true); " +
                "if [ -n \"$response\" ]; then printf '%s\\n' \"$response\"; exit 0; fi; " +
                "done; exit 1;"
            ],
            $"Verify public IP through Home Lab container {containerName}",
            cancellationToken);
        var publicIp = HomeLabPublicIpParser.Parse(result.StandardOutput);
        if (publicIp is not null)
        {
            return new HomeLabPublicIpProbeResult(publicIp, "The gateway returned a valid egress IP.");
        }

        var logs = await RunDockerAsync(
            ["logs", "--tail", "200", containerName],
            $"Read Gluetun public IP status for {containerName}",
            cancellationToken);
        var loggedPublicIp = HomeLabPublicIpParser.ParseGluetunLogs(
            $"{logs.StandardOutput}\n{logs.StandardError}");
        if (loggedPublicIp is not null)
        {
            return new HomeLabPublicIpProbeResult(
                loggedPublicIp,
                "The gateway's latest Gluetun log reported a valid egress IP.");
        }

        var failure = result.ExitCode == 0
            ? "Gluetun returned no valid IP value. The VPN may still be connecting, or public-IP detection may be disabled."
            : FirstNonEmpty(result.StandardError, result.StandardOutput, $"Docker exited with code {result.ExitCode}.");
        logger.LogWarning(
            "Home Lab public IP probe failed for {ContainerName}: exit code {ExitCode}; {Failure}",
            containerName,
            result.ExitCode,
            failure);
        return new HomeLabPublicIpProbeResult(null, failure);
    }

    private sealed record HomeLabPublicIpProbeResult(string? PublicIp, string Detail);

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

    private static IReadOnlyList<HomeLabPortBinding> ParsePortBindings(
        JsonObject inspect,
        HomeLabAppManifest app,
        bool useVpnNamespacePort)
    {
        var ports = inspect["NetworkSettings"]?["Ports"]?.AsObject();
        if (ports is null)
        {
            return [];
        }

        var result = new List<HomeLabPortBinding>();
        foreach (var port in app.Ports)
        {
            var containerPort = HomeLabContainerPortPlan.Resolve(port, useVpnNamespacePort);
            var key = $"{containerPort}/{port.Protocol}";
            var published = ports[key]?.AsArray()?.FirstOrDefault()?.AsObject()?["HostPort"]?.GetValue<string>();
            _ = int.TryParse(published, out var hostPort);
            result.Add(new HomeLabPortBinding(port.Name, containerPort, hostPort));
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

    private static HomeLabPortManifest? ResolvePrimaryPort(HomeLabAppManifest app) =>
        app.Ports.FirstOrDefault(port => port.Primary) ?? app.Ports.FirstOrDefault();

    private static string ResolveApplicationScheme(HomeLabPortManifest port) =>
        !string.IsNullOrWhiteSpace(port.ApplicationProtocol)
            ? port.ApplicationProtocol.Trim().ToLowerInvariant()
            : port.Name.Equals("web", StringComparison.OrdinalIgnoreCase)
                ? "http"
                : port.Protocol.ToLowerInvariant();

    private static IEnumerable<KeyValuePair<string, string>> BuildVpnPortEnvironment(
        HomeLabAppManifest app,
        string networkMode)
    {
        if (!networkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return app.Ports
            .Where(port => !string.IsNullOrWhiteSpace(port.VpnEnvironmentVariable) && port.VpnContainerPort is not null)
            .Select(port => new KeyValuePair<string, string>(
                port.VpnEnvironmentVariable!,
                HomeLabContainerPortPlan.Resolve(port, true).ToString()));
    }

    private static IEnumerable<KeyValuePair<string, string>> BuildGatewayEnvironment(HomeLabAppManifest app)
    {
        if (!app.Id.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return
        [
            new KeyValuePair<string, string>(
                "FIREWALL_INPUT_PORTS",
                HomeLabContainerPortPlan.GatewayFirewallInputPorts())
        ];
    }

    private static IEnumerable<KeyValuePair<string, string>> BuildPublicUrlEnvironment(
        HomeLabAppManifest app,
        HomeLabInstallationEntity installation)
    {
        if (string.IsNullOrWhiteSpace(app.PublicUrlEnvironmentVariable) ||
            installation.CaddySourcePort is not int caddyPort)
        {
            return [];
        }

        var clientEndpoint = installation.ServiceEndpoints.FirstOrDefault(endpoint =>
            endpoint.Scope == (int)HomeLabEndpointScope.Client &&
            endpoint.PortName.Equals(
                HomeLabEndpointPlanner.ResolveClientAccess(app)?.PortName ?? "web",
                StringComparison.OrdinalIgnoreCase));
        return
        [
            new KeyValuePair<string, string>(
                app.PublicUrlEnvironmentVariable,
                clientEndpoint?.Url ?? $"http://{Dns.GetHostName()}:{caddyPort}")
        ];
    }

    private string ResolveEndpointTokens(HomeLabInstallationEntity current, string value) =>
        HomeLabEndpointPlanner.RenderEndpointTokens(
            value,
            current.AppId,
            (serviceId, scope) => ResolveEndpointForTemplate(current, serviceId, scope));

    private HomeLabServiceEndpoint? ResolveEndpointForTemplate(
        HomeLabInstallationEntity current,
        string serviceId,
        HomeLabEndpointScope scope)
    {
        var installation = current.AppId.Equals(serviceId, StringComparison.OrdinalIgnoreCase)
            ? current
            : current.Deployment?.Installations.FirstOrDefault(item =>
                  item.AppId.Equals(serviceId, StringComparison.OrdinalIgnoreCase))
              ?? dbContext.HomeLabInstallations.Local.FirstOrDefault(item =>
                  item.DeploymentId == current.DeploymentId &&
                  item.AppId.Equals(serviceId, StringComparison.OrdinalIgnoreCase));
        if (installation is null)
        {
            return null;
        }

        var app = HomeLabCatalog.GetApp(installation.AppId);
        var clientAccess = HomeLabEndpointPlanner.ResolveClientAccess(app);
        var portName = scope == HomeLabEndpointScope.Internal
            ? (ResolvePrimaryPort(app)?.Name ?? string.Empty)
            : clientAccess?.PortName ?? string.Empty;
        var persisted = installation.ServiceEndpoints.FirstOrDefault(endpoint =>
            endpoint.Scope == (int)scope &&
            (portName.Length == 0 || endpoint.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase)));
        if (persisted is not null)
        {
            return MapServiceEndpoint(persisted);
        }

        var now = DateTimeOffset.UtcNow;
        if (scope == HomeLabEndpointScope.Internal && ResolvePrimaryPort(app) is { } internalPort)
        {
            var host = HomeLabEndpointPlanner.ResolveInternalHost(installation);
            var port = HomeLabEndpointPlanner.ResolveInternalPort(internalPort, installation);
            var scheme = ResolveApplicationScheme(internalPort);
            return new HomeLabServiceEndpoint(
                Guid.Empty, installation.Id, installation.AppId, internalPort.Name, scope,
                $"{scheme}://{host}:{port}", scheme, host, port, string.Empty, null, null,
                HomeLabEndpointHealthState.NotChecked, string.Empty, null, now, now);
        }

        if (scope is HomeLabEndpointScope.Lan or HomeLabEndpointScope.Client &&
            clientAccess is not null && installation.CaddySourcePort is int caddyPort)
        {
            var host = Dns.GetHostName();
            return new HomeLabServiceEndpoint(
                Guid.Empty, installation.Id, installation.AppId, clientAccess.PortName, scope,
                $"http://{host}:{caddyPort}", "http", host, caddyPort, string.Empty, null, null,
                HomeLabEndpointHealthState.NotChecked, string.Empty, null, now, now);
        }

        return null;
    }

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

        return Path.Combine(ResolveHomeLabFilesRoot(storagePaths), BuildContainerName(deploymentId, app.Id), volume.Id);
    }

    private string ResolveHomeLabFilesRoot(IReadOnlyDictionary<string, string>? storagePaths = null)
    {
        var supplied = storagePaths?.FirstOrDefault(item =>
            item.Key.Equals(HomeLabFilesRole, StringComparison.OrdinalIgnoreCase)).Value;
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            return NormalizeHostPath(supplied);
        }

        var stored = dbContext.HomeLabStorageRoles
            .AsNoTracking()
            .SingleOrDefault(item => item.Role == HomeLabFilesRole);
        return string.IsNullOrWhiteSpace(stored?.HostPath)
            ? DefaultHomeLabFilesRoot
            : NormalizeHostPath(stored.HostPath);
    }

    private static IReadOnlyList<HomeLabStorageRole> BuildStorageRoles(
        IReadOnlyList<HomeLabStorageRoleEntity> entities)
    {
        var roles = entities.Select(MapStorageRole).ToList();
        if (roles.All(item => !item.Role.Equals(HomeLabFilesRole, StringComparison.OrdinalIgnoreCase)))
        {
            roles.Add(new HomeLabStorageRole(HomeLabFilesRole, DefaultHomeLabFilesRoot, DateTimeOffset.MinValue));
        }

        return roles.OrderBy(item => item.Role, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task EnsureBrowsableVolumeLayoutAsync(
        IReadOnlyList<HomeLabInstallationEntity> installations,
        CancellationToken cancellationToken)
    {
        if (installations.Count == 0)
        {
            return;
        }

        var rootPath = ResolveHomeLabFilesRoot();
        foreach (var installation in installations)
        {
            var app = HomeLabCatalog.GetApp(installation.AppId);
            var containerRoot = Path.Combine(rootPath, installation.ContainerName);
            if (!Directory.Exists(containerRoot))
            {
                var mkdir = await RunAsync(
                    new LinuxCommandRequest(
                        "mkdir",
                        ["-p", containerRoot],
                        true,
                        TimeSpan.FromSeconds(30),
                        $"Prepare browsable files for {installation.DisplayName}"),
                    cancellationToken);
                EnsureSuccess(mkdir, $"The files folder for '{installation.DisplayName}' could not be prepared.");
            }

            foreach (var binding in DeserializeBindings(installation.VolumeMappingsJson))
            {
                if (IsStandardStorageBinding(binding))
                {
                    continue;
                }

                var volume = app.Volumes.FirstOrDefault(item =>
                    item.ContainerPath.Equals(binding.ContainerPath, StringComparison.Ordinal));
                var volumeName = volume?.Id ?? BuildBrowsableVolumeName(binding.ContainerPath);
                var browsePath = Path.Combine(containerRoot, volumeName);
                if (Path.GetFullPath(binding.HostPath).Equals(Path.GetFullPath(browsePath), StringComparison.Ordinal) ||
                    Directory.Exists(browsePath) || File.Exists(browsePath) || new FileInfo(browsePath).LinkTarget is not null)
                {
                    continue;
                }

                var link = await RunAsync(
                    new LinuxCommandRequest(
                        "ln",
                        ["--symbolic", "--force", "--no-dereference", "--no-target-directory", binding.HostPath, browsePath],
                        true,
                        TimeSpan.FromSeconds(30),
                        $"Link {installation.DisplayName} {volumeName} into its browsable files folder"),
                    cancellationToken);
                EnsureSuccess(link, $"The files link for '{installation.DisplayName}' volume '{volumeName}' could not be prepared.");
            }
        }
    }

    private static string BuildBrowsableVolumeName(string containerPath)
    {
        var leaf = Path.GetFileName(containerPath.TrimEnd('/'));
        var normalized = Regex.Replace(leaf, "[^a-zA-Z0-9._-]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "files" : normalized;
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
                : "Paste provider config";
            var protocol = RequiredValue(supplied, "protocol", "Choose a VPN protocol.");
            var protocolValue = protocol.Equals("WireGuard", StringComparison.OrdinalIgnoreCase) ? "wireguard" :
                protocol.Equals("OpenVPN", StringComparison.OrdinalIgnoreCase) ? "openvpn" :
                throw new InvalidOperationException("Choose WireGuard or OpenVPN.");
            var provider = RequiredValue(supplied, "provider", "Choose the provider that issued this VPN configuration.");

            if (setupMode.Equals("Paste provider config", StringComparison.OrdinalIgnoreCase))
            {
                values["VPN_SERVICE_PROVIDER"] = "custom";
                values["VPN_TYPE"] = protocolValue;
                if (!suppliedSecrets.TryGetValue("vpn-config", out var pastedConfig) || string.IsNullOrWhiteSpace(pastedConfig))
                {
                    throw new InvalidOperationException("Paste the provider configuration file.");
                }
                ConfigureVpnPortForwarding(values, supplied, provider, protocolValue, pastedConfig);

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

            ConfigureVpnPortForwarding(values, supplied, provider, protocolValue, null);
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

    private async Task RefreshWireGuardProfileAsync(
        HomeLabInstallationEntity installation,
        CancellationToken cancellationToken)
    {
        var secretReference = DeserializeDictionary(installation.SecretConfigurationJson)
            .SingleOrDefault(item => item.Key.Equals("FILE:/gluetun/wireguard/wg0.conf", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(secretReference.Key))
        {
            return;
        }

        var profile = await secretStore.ResolveSecretAsync(secretReference.Value, cancellationToken);
        if (string.IsNullOrEmpty(profile))
        {
            throw new InvalidOperationException("The saved WireGuard profile is unavailable. Edit the VPN Gateway and paste the provider profile again.");
        }

        var bindings = DeserializeBindings(installation.VolumeMappingsJson);
        var hostPath = ResolveSecretFileHostPath(bindings, "/gluetun/wireguard/wg0.conf");
        var normalizedProfile = await HomeLabWireGuardProfileNormalizer.NormalizeAsync(profile, cancellationToken);
        await WriteSecretFileAsync(hostPath, normalizedProfile, cancellationToken);
    }

    private static Task<string> PrepareSecretFileContentAsync(
        HomeLabAppManifest app,
        string containerPath,
        string content,
        CancellationToken cancellationToken) =>
        app.Id.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase) &&
        containerPath.Equals("/gluetun/wireguard/wg0.conf", StringComparison.OrdinalIgnoreCase)
            ? HomeLabWireGuardProfileNormalizer.NormalizeAsync(content, cancellationToken)
            : Task.FromResult(content);

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

    private static string NormalizeContainerImage(string image)
    {
        var normalized = image?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 512 || normalized.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new InvalidOperationException("Enter a valid Docker image reference without spaces.");
        }

        return normalized;
    }

    private static IReadOnlyDictionary<string, string> NormalizeContainerEnvironment(
        IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null || environment.Count > 128)
        {
            throw new InvalidOperationException("Container environment settings must contain no more than 128 entries.");
        }

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in environment)
        {
            var key = item.Key?.Trim() ?? string.Empty;
            var value = item.Value ?? string.Empty;
            if (key.Length is < 1 or > 128 ||
                !(char.IsAsciiLetter(key[0]) || key[0] == '_') ||
                key.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
            {
                throw new InvalidOperationException($"'{key}' is not a valid container environment variable name.");
            }
            if (value.Length > 32768 || value.Contains('\0'))
            {
                throw new InvalidOperationException($"The value for '{key}' is too large or contains an invalid character.");
            }

            normalized[key] = value;
        }

        return normalized;
    }

    private static IReadOnlyList<HomeLabContainerVolumeSetting> NormalizeContainerVolumes(
        IReadOnlyList<HomeLabContainerVolumeSetting>? volumes)
    {
        if (volumes is null || volumes.Count > 64)
        {
            throw new InvalidOperationException("Container volume settings must contain no more than 64 mappings.");
        }

        var normalized = new List<HomeLabContainerVolumeSetting>(volumes.Count);
        var containerPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var volume in volumes)
        {
            var hostPath = volume.HostPath?.Trim() ?? string.Empty;
            var containerPath = volume.ContainerPath?.Trim() ?? string.Empty;
            if (!Path.IsPathFullyQualified(hostPath) || !Path.IsPathFullyQualified(containerPath) ||
                hostPath.Contains(':') || containerPath.Contains(':') ||
                hostPath.Any(char.IsControl) || containerPath.Any(char.IsControl))
            {
                throw new InvalidOperationException("Volume mappings must use absolute host and container paths without colons.");
            }
            if (!containerPaths.Add(containerPath))
            {
                throw new InvalidOperationException($"Container path '{containerPath}' is mapped more than once.");
            }

            normalized.Add(new HomeLabContainerVolumeSetting(Path.GetFullPath(hostPath), containerPath, volume.ReadOnly));
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
        key.Equals("gateway-name", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("lms-container-backup-", StringComparison.OrdinalIgnoreCase);

    private static void ConfigureVpnPortForwarding(
        IDictionary<string, string> values,
        IReadOnlyDictionary<string, string> supplied,
        string provider,
        string protocol,
        string? pastedConfiguration)
    {
        var selection = RequiredValue(supplied, "port-forwarding", "Choose whether this gateway must support an incoming P2P port.");
        foreach (var item in HomeLabVpnPortForwardingPlan.Build(selection, provider, protocol, pastedConfiguration))
        {
            values[item.Key] = item.Value;
        }
    }

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
            var existingBindings = DeserializePortBindings(existing.PortMappingsJson);
            var conflictingPort = app.Ports.FirstOrDefault(port =>
                existingApp.Ports.Any(existingPort =>
                {
                    if (!existingPort.Protocol.Equals(port.Protocol, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    var existingBinding = existingBindings.FirstOrDefault(binding =>
                        binding.Name.Equals(existingPort.Name, StringComparison.OrdinalIgnoreCase));
                    var existingContainerPort = existingBinding?.ContainerPort ??
                        HomeLabContainerPortPlan.Resolve(existingPort, true);
                    return existingContainerPort == HomeLabContainerPortPlan.Resolve(port, true);
                }));
            if (conflictingPort is not null)
            {
                throw new InvalidOperationException(
                    $"{app.Name} cannot share the VPN Gateway network namespace with {existingApp.Name}: both require {conflictingPort.Protocol.ToUpperInvariant()} port {HomeLabContainerPortPlan.Resolve(conflictingPort, true)} and neither app has a separate configured listener. Update the app definition or use a separate VPN Gateway installation.");
            }
        }
    }

    private async Task<HomeLabOperationResult> EnsureVpnGatewayNamespacePortAsync(
        HomeLabInstallationEntity gateway,
        HomeLabAppManifest app,
        List<string> output,
        bool pullImage,
        CancellationToken cancellationToken)
    {
        // Validate the complete shared namespace before accepting an already-published port.
        // This prevents a catalog collision from being hidden by an existing gateway binding.
        _ = HomeLabContainerPortPlan.GatewayPublishedPorts();

        var requiredPorts = app.Ports
            .Select(port => (Protocol: port.Protocol.ToLowerInvariant(), ContainerPort: HomeLabContainerPortPlan.Resolve(port, true)))
            .Distinct()
            .ToArray();
        if (requiredPorts.Length == 0)
        {
            return Success("VPN Gateway ports ready.", "The app does not expose a listener through the VPN namespace.", [], HomeLabHealthState.Healthy);
        }

        var inspect = await InspectContainerAsync(gateway.ContainerName, cancellationToken);
        var publishedPorts = inspect?["NetworkSettings"]?["Ports"]?.AsObject();
        var missing = requiredPorts.Any(required =>
            publishedPorts?[$"{required.ContainerPort}/{required.Protocol}"]?.AsArray()?.Count > 0 != true);
        if (!missing)
        {
            return Success("VPN Gateway ports ready.", $"{app.Name} can use the existing gateway namespace.", [], HomeLabHealthState.Healthy);
        }

        var trackedGateway = await dbContext.HomeLabInstallations
            .SingleAsync(item => item.Id == gateway.Id, cancellationToken);
        return await RecreateVpnGatewayAsync(trackedGateway, output, pullImage, cancellationToken);
    }

    private static IReadOnlyList<(string Protocol, int Port)> FindMissingVpnGatewayPorts(JsonObject inspect)
    {
        var publishedPorts = inspect["NetworkSettings"]?["Ports"]?.AsObject();
        return HomeLabContainerPortPlan.GatewayPublishedPorts()
            .Where(required =>
                publishedPorts?[$"{required.Port}/{required.Protocol}"]?.AsArray()?.Count > 0 != true)
            .ToArray();
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

    private static string QbittorrentRestartLoginNote(HomeLabAppManifest app) =>
        app.Id.Equals("qbittorrent", StringComparison.OrdinalIgnoreCase)
            ? " If qBittorrent still uses its temporary WebUI password, open Settings and use the new password generated by this restart."
            : string.Empty;

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
        new(item.Id, item.Name, item.RecipeId, item.RecipeRunId, item.PromptRecipeId, item.NetworkName, item.CreatedAtUtc, item.UpdatedAtUtc);

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
            item.IsRecipeInstallation,
            item.ServiceEndpoints
                .OrderBy(endpoint => endpoint.Scope)
                .ThenBy(endpoint => endpoint.PortName, StringComparer.OrdinalIgnoreCase)
                .Select(MapServiceEndpoint)
                .ToArray());

    private static HomeLabServiceEndpoint MapServiceEndpoint(HomeLabServiceEndpointEntity item) =>
        new(
            item.Id,
            item.InstallationId,
            item.ServiceId,
            item.PortName,
            Enum.IsDefined(typeof(HomeLabEndpointScope), item.Scope)
                ? (HomeLabEndpointScope)item.Scope
                : HomeLabEndpointScope.Internal,
            item.Url,
            item.Scheme,
            item.Host,
            item.Port,
            item.PathBase,
            item.RoutingMode.HasValue && Enum.IsDefined(typeof(HomeLabClientRoutingStrategy), item.RoutingMode.Value)
                ? (HomeLabClientRoutingStrategy)item.RoutingMode.Value
                : null,
            item.EdgeGatewayRouteId,
            Enum.IsDefined(typeof(HomeLabEndpointHealthState), item.HealthState)
                ? (HomeLabEndpointHealthState)item.HealthState
                : HomeLabEndpointHealthState.NotChecked,
            item.HealthDetail,
            item.CheckedAtUtc,
            item.CreatedAtUtc,
            item.UpdatedAtUtc);

    private static Dictionary<string, string> DeserializeDictionary(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
        ?? new(StringComparer.OrdinalIgnoreCase);

    private static PublishedHomeLabRecipeConnectivity? DeserializePublishedConnectivity(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<PublishedHomeLabRecipeConnectivity>(json, JsonOptions);

    private static HomeLabClientAccessManifest? ResolveRecipeClientAccess(
        HomeLabAppManifest app,
        PublishedHomeLabRecipeConnectivity? connectivity)
    {
        if (connectivity is null)
        {
            return HomeLabEndpointPlanner.ResolveClientAccess(app);
        }

        var service = connectivity.Services.FirstOrDefault(item =>
            item.ServiceId.Equals(app.Id, StringComparison.OrdinalIgnoreCase));
        if (service is null || !service.ClientAccessEnabled ||
            !service.Scopes.Contains("client", StringComparer.OrdinalIgnoreCase) ||
            !service.Scopes.Contains("public", StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var access = HomeLabEndpointPlanner.ResolveClientAccess(app);
        if (access is null)
        {
            var port = app.Ports.FirstOrDefault(item => item.Primary) ?? app.Ports.FirstOrDefault();
            if (port is null || !port.Protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            access = new HomeLabClientAccessManifest(
                true,
                port.Name,
                HomeLabClientRoutingStrategy.Auto,
                $"/{app.Id}",
                new HomeLabReverseProxyManifest(HomeLabBasePathSupportMode.None));
        }

        if (!Enum.TryParse<HomeLabClientRoutingStrategy>(service.Routing, true, out var routing))
        {
            throw new InvalidOperationException($"Recipe service '{service.ServiceId}' has an invalid routing strategy.");
        }

        return access with
        {
            Routing = routing,
            PreferredPath = string.IsNullOrWhiteSpace(service.PreferredPath)
                ? access.PreferredPath
                : service.PreferredPath
        };
    }

    private static IReadOnlyList<HomeLabVolumeBinding> DeserializeBindings(string json) =>
        EnsureStandardStorageBinding(JsonSerializer.Deserialize<List<HomeLabVolumeBinding>>(json, JsonOptions) ?? []);

    private static IReadOnlyList<HomeLabVolumeBinding> EnsureStandardStorageBinding(
        IEnumerable<HomeLabVolumeBinding> bindings) =>
        bindings
            .Where(binding => !binding.ContainerPath.Equals(StandardStorageContainerPath, StringComparison.Ordinal))
            .Append(new HomeLabVolumeBinding(StandardStorageHostPath, StandardStorageContainerPath, false))
            .ToArray();

    private static bool IsStandardStorageBinding(HomeLabVolumeBinding binding) =>
        binding.HostPath.Equals(StandardStorageHostPath, StringComparison.Ordinal) &&
        binding.ContainerPath.Equals(StandardStorageContainerPath, StringComparison.Ordinal);

    private static IReadOnlyList<HomeLabPortBinding> DeserializePortBindings(string json) =>
        JsonSerializer.Deserialize<List<HomeLabPortBinding>>(json, JsonOptions) ?? [];

    private sealed record HomeLabVolumeBinding(string HostPath, string ContainerPath, bool ReadOnly);
    private sealed record HomeLabPortBinding(string Name, int ContainerPort, int HostPort);
    private sealed record HomeLabConfigurationRoot(string Id, string HostPath);
    private sealed record EndpointCapabilityValidationResult(
        bool Succeeded,
        bool ProbeFailed,
        IReadOnlyList<string> Messages);

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
