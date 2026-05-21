// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Application.Contracts.EdgeGateway;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Application.Services;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.Cloudflare;
using LinuxMadeSane.Core.Models.EdgeGateway;

namespace LinuxMadeSane.Application.Services.EdgeGateway;

public sealed class EdgeGatewayService(
    IEdgeGatewayStore store,
    IEdgeGatewayCaddyManager caddyManager,
    ICaddyIntegrationService caddyIntegrationService,
    EdgeGatewayCaddyfileGenerator caddyfileGenerator,
    IExposedServiceManager exposedServiceManager,
    ICloudflareExposureStore cloudflareExposureStore,
    ISecretStore secretStore,
    ICloudflareTunnelService cloudflareTunnelService,
    ICloudflareDnsService cloudflareDnsService,
    ICommandExecutionService commandExecutionService,
    CloudflareIntegrationOptions cloudflareOptions,
    IEdgeGatewaySettingsStore settingsStore,
    EdgeGatewayOptions options) : IEdgeGatewayService
{
    private const int StatusOk = 200;
    private const int StatusFound = 302;
    private const int StatusForbidden = 403;
    private const int StatusNotFound = 404;

    public async Task<EdgeGatewayDashboardViewModel> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var settingsTask = GetGatewaySettingsAsync(cancellationToken);
        var routesTask = store.ListRoutesAsync(cancellationToken);
        var auditEntriesTask = store.ListAuditEntriesAsync(take: 80, cancellationToken: cancellationToken);

        var settings = await settingsTask;
        var connectorStatusTask = TryInspectLocalCloudflaredConnectorAsync(cancellationToken);
        var gatewaySubdomain = settings.GatewaySubdomain;
        var cloudflareTask = BuildCloudflareStatusAsync(settings, connectorStatusTask, cancellationToken);
        var runtimeTask = BuildRuntimeStatusAsync(connectorStatusTask, cancellationToken);

        await Task.WhenAll(routesTask, auditEntriesTask, cloudflareTask, runtimeTask);
        var routes = await routesTask;
        var auditEntries = await auditEntriesTask;
        var cloudflare = await cloudflareTask;
        var runtime = await runtimeTask;
        var generatedCaddyfile = caddyfileGenerator.Generate(routes);
        var firstDomain = routes.Select(static route => route.DomainName)
            .Concat(cloudflare.Domains.Select(static domain => domain.GatewayDomainName))
            .FirstOrDefault(static domain => !string.IsNullOrWhiteSpace(domain));
        var tunnelSnippet = string.IsNullOrWhiteSpace(firstDomain)
            ? "Add a route domain first, then LMS can generate the matching cloudflared ingress snippet."
            : caddyfileGenerator.GenerateCloudflareTunnelSnippet(firstDomain);

        return new EdgeGatewayDashboardViewModel(
            new EdgeGatewaySettingsViewModel(
                gatewaySubdomain,
                $"{gatewaySubdomain}.example.com"),
            routes.Select(MapListItem).ToArray(),
            auditEntries,
            cloudflare,
            runtime,
            generatedCaddyfile,
            tunnelSnippet,
            $"Create a scoped proxied wildcard record such as *.{gatewaySubdomain}.example.com -> <tunnel-id>.cfargotunnel.com");
    }

    public async Task<EdgeGatewaySettingsEditor> GetSettingsEditorAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetGatewaySettingsAsync(cancellationToken);
        return new EdgeGatewaySettingsEditor
        {
            GatewaySubdomain = settings.GatewaySubdomain
        };
    }

    public async Task SaveSettingsAsync(EdgeGatewaySettingsEditor editor, CancellationToken cancellationToken = default)
    {
        var current = await GetGatewaySettingsAsync(cancellationToken);
        var gatewaySubdomain = NormalizeGatewaySubdomain(editor.GatewaySubdomain);
        await settingsStore.SaveAsync(current with
        {
            GatewaySubdomain = gatewaySubdomain,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public Task<CloudflareValidationResult> ValidateCloudflareTokenAsync(
        string apiToken,
        bool saveToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiToken))
        {
            throw new InvalidOperationException("Paste a Cloudflare API token before validating.");
        }

        return exposedServiceManager.ValidateTokenAsync(
            AiLocalMachine.ManagedHostId,
            apiToken.Trim(),
            saveToken,
            cancellationToken);
    }

    public async Task ResetSetupAsync(CancellationToken cancellationToken = default)
    {
        var current = await GetGatewaySettingsAsync(cancellationToken);
        var routes = await store.ListRoutesAsync(cancellationToken);
        await ResetLiveCloudflareResourcesAsync(current, routes, cancellationToken);

        foreach (var route in routes)
        {
            await store.DeleteRouteAsync(route.Id, cancellationToken);
        }

        var caddyApplyResult = await ApplyCaddyConfigurationAsync(cancellationToken);
        if (!caddyApplyResult.Success)
        {
            throw new InvalidOperationException($"Cloudflare resources were reset, but Caddy did not reload cleanly: {caddyApplyResult.Summary}");
        }

        await exposedServiceManager.ForgetSavedTokenAsync(AiLocalMachine.ManagedHostId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        await settingsStore.SaveAsync(
            current with
            {
                GatewaySubdomain = EdgeGatewayDefaultNamespace.BuildForMachineName(Environment.MachineName),
                TunnelInstanceId = Guid.NewGuid().ToString("N"),
                UpdatedAtUtc = now
            },
            cancellationToken);
    }

    private async Task ResetLiveCloudflareResourcesAsync(
        EdgeGatewaySettings settings,
        IReadOnlyList<EdgeGatewayRoute> routes,
        CancellationToken cancellationToken)
    {
        string apiToken;
        try
        {
            apiToken = await ResolveSavedCloudflareTokenAsync(cancellationToken);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("No saved Cloudflare API token", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var validation = await exposedServiceManager.ValidateTokenAsync(AiLocalMachine.ManagedHostId, null, cancellationToken);
        foreach (var zone in validation.Zones)
        {
            var account = ResolveCloudflareAccount(validation.Accounts, zone);
            if (account is null)
            {
                throw new InvalidOperationException($"Cloudflare did not return an account that owns {zone.Name}.");
            }

            await ResetLiveCloudflareZoneAsync(apiToken, account, zone, settings, routes, cancellationToken);
        }
    }

    private async Task ResetLiveCloudflareZoneAsync(
        string apiToken,
        CloudflareAccount account,
        CloudflareZone zone,
        EdgeGatewaySettings settings,
        IReadOnlyList<EdgeGatewayRoute> routes,
        CancellationToken cancellationToken)
    {
        string gatewayDomainName;
        try
        {
            gatewayDomainName = BuildGatewayDomainName(zone.Name, settings.GatewaySubdomain);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var wildcardHostname = $"*.{gatewayDomainName}";
        var tunnelBaseName = BuildEdgeTunnelBaseName(gatewayDomainName, settings.TunnelInstanceId);
        var tunnels = await cloudflareTunnelService.ListTunnelsAsync(apiToken, account.Id, cancellationToken);
        var records = await cloudflareDnsService.ListRecordsAsync(apiToken, zone.Id, cancellationToken);
        var relayNamespaceTunnelIds = records
            .Where(record => record.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase) &&
                             IsDnsRecordInRelayNamespace(record, gatewayDomainName))
            .Select(record => TryExtractTunnelIdFromDnsTarget(record.Content))
            .Where(static tunnelId => !string.IsNullOrWhiteSpace(tunnelId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownedTunnels = tunnels
            .Where(tunnel => !tunnel.IsDeleted &&
                             (tunnel.Name.Equals(tunnelBaseName, StringComparison.OrdinalIgnoreCase) ||
                              relayNamespaceTunnelIds.Contains(tunnel.Id)))
            .ToArray();
        var ownedTunnelDnsTargets = ownedTunnels
            .Select(static tunnel => $"{tunnel.Id}.cfargotunnel.com")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var routeHostnames = routes
            .Where(route => route.DomainName.Equals(zone.Name, StringComparison.OrdinalIgnoreCase))
            .Select(static route => EdgeGatewayRouteValidator.NormalizeHostname(route.Hostname))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var routeRelayHostnames = routes
            .Where(route => route.DomainName.Equals(zone.Name, StringComparison.OrdinalIgnoreCase))
            .Select(route => $"{ResolveRelativeHostname(EdgeGatewayRouteValidator.NormalizeHostname(route.Hostname), zone.Name)}.{gatewayDomainName}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var recordsToDelete = records
            .Where(record => IsDnsRecordInRelayNamespace(record, gatewayDomainName) ||
                             IsDnsRecordPointingIntoRelayNamespace(record, gatewayDomainName) ||
                             IsResettableEdgeGatewayDnsRecord(record, routeHostnames, routeRelayHostnames, ownedTunnelDnsTargets))
            .GroupBy(static record => record.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();

        foreach (var record in recordsToDelete)
        {
            await cloudflareDnsService.DeleteRecordAsync(apiToken, zone.Id, record.Id, cancellationToken);
        }

        var tunnelRouteHostnames = recordsToDelete
            .Select(static record => (record.Name ?? string.Empty).Trim().TrimEnd('.'))
            .Where(static hostname => !string.IsNullOrWhiteSpace(hostname))
            .Concat(routeHostnames)
            .Append(wildcardHostname)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var tunnel in ownedTunnels)
        {
            var configuration = await cloudflareTunnelService.GetConfigurationAsync(apiToken, account.Id, tunnel.Id, cancellationToken);
            var updatedRoutes = RemoveTunnelRoutes(configuration.Routes, tunnelRouteHostnames);
            if (updatedRoutes.Count != configuration.Routes.Count)
            {
                await cloudflareTunnelService.UpdateConfigurationAsync(
                    apiToken,
                    account.Id,
                    tunnel.Id,
                    new CloudflareTunnelConfiguration(updatedRoutes),
                    cancellationToken);
            }

            await cloudflareTunnelService.DeleteTunnelAsync(apiToken, account.Id, tunnel.Id, cancellationToken);
        }
    }

    public async Task<EdgeGatewayRouteEditor> GetEditorAsync(Guid? routeId, CancellationToken cancellationToken = default)
    {
        if (!routeId.HasValue)
        {
            return new EdgeGatewayRouteEditor();
        }

        var route = await store.GetRouteAsync(routeId.Value, cancellationToken);
        return route is null
            ? new EdgeGatewayRouteEditor()
            : new EdgeGatewayRouteEditor
            {
                Id = route.Id,
                Enabled = route.Enabled,
                DisplayName = route.DisplayName,
                Hostname = route.Hostname,
                DomainName = route.DomainName,
                TargetScheme = route.TargetScheme,
                TargetHost = route.TargetHost,
                TargetPort = route.TargetPort,
                TargetPathPrefix = route.TargetPathPrefix,
                AuthMode = NormalizePublicAuthMode(route.AuthMode),
                AllowedUsers = route.AllowedUsers,
                AllowedGroups = route.AllowedGroups,
                AllowLanOnly = route.AllowLanOnly,
                AllowKnownIps = route.AllowKnownIps,
                Notes = route.Notes
            };
    }

    public async Task<Guid> SaveRouteAsync(EdgeGatewayRouteEditor editor, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var routeId = editor.Id ?? Guid.NewGuid();
        var existing = editor.Id.HasValue ? await store.GetRouteAsync(editor.Id.Value, cancellationToken) : null;
        var domainName = EdgeGatewayRouteValidator.NormalizeDomainName(editor.DomainName);
        var hostname = NormalizeHostnameOrSubdomain(editor.Hostname, domainName);
        var targetPathPrefix = EdgeGatewayRouteValidator.NormalizePathPrefix(editor.TargetPathPrefix);
        await EnsureRoutePathIsAvailableAsync(routeId, hostname, targetPathPrefix, cancellationToken);
        var route = new EdgeGatewayRoute(
            routeId,
            editor.Enabled,
            NormalizeRequiredText(editor.DisplayName, "Enter a display name."),
            hostname,
            domainName,
            editor.TargetScheme,
            EdgeGatewayRouteValidator.NormalizeTargetHost(editor.TargetHost),
            EdgeGatewayRouteValidator.NormalizeTargetPort(editor.TargetPort),
            targetPathPrefix,
            NormalizePublicAuthMode(editor.AuthMode),
            NormalizeCsvOrLines(editor.AllowedUsers),
            NormalizeCsvOrLines(editor.AllowedGroups),
            editor.AllowLanOnly,
            NormalizeCsvOrLines(editor.AllowKnownIps),
            (editor.Notes ?? string.Empty).Trim(),
            existing?.CreatedAt ?? now,
            now,
            existing?.LastTestStatus ?? EdgeGatewayDiagnosticStatus.NotConfigured,
            existing?.LastTestMessage ?? string.Empty);

        EdgeGatewayRouteValidator.ValidateRoute(route);
        await store.SaveRouteAsync(route, cancellationToken);
        return route.Id;
    }

    private async Task EnsureRoutePathIsAvailableAsync(
        Guid routeId,
        string hostname,
        string targetPathPrefix,
        CancellationToken cancellationToken)
    {
        var routes = await store.ListRoutesAsync(cancellationToken);
        var duplicate = routes.FirstOrDefault(route =>
            route.Id != routeId &&
            route.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase) &&
            EdgeGatewayRouteValidator.NormalizePathPrefix(route.TargetPathPrefix)
                .Equals(targetPathPrefix, StringComparison.OrdinalIgnoreCase));

        if (duplicate is not null)
        {
            var path = string.IsNullOrWhiteSpace(targetPathPrefix) ? "/" : targetPathPrefix;
            throw new InvalidOperationException($"An Edge Gateway route already exists for {hostname}{path}.");
        }
    }

    private async Task<EdgeGatewayCloudflareStatus> BuildCloudflareStatusAsync(
        EdgeGatewaySettings settings,
        Task<CloudflaredConnectorStatus?> connectorStatusTask,
        CancellationToken cancellationToken)
    {
        try
        {
            var validation = await exposedServiceManager.ValidateTokenAsync(AiLocalMachine.ManagedHostId, null, cancellationToken);
            if (validation.Zones.Count == 0)
            {
                return new EdgeGatewayCloudflareStatus(
                    validation.HasSavedToken,
                    false,
                    validation.HasSavedToken
                        ? "The saved Cloudflare token validated but did not return any zones. Check the token zone scope."
                        : "No saved Cloudflare token is registered yet. Validate a token in Edge Gateway Setup first.",
                    []);
            }

            var apiToken = validation.HasSavedToken
                ? await ResolveSavedCloudflareTokenAsync(cancellationToken)
                : string.Empty;
            var connectorStatus = await connectorStatusTask;
            var domainTasks = validation.Zones
                .OrderBy(static zone => zone.Name, StringComparer.OrdinalIgnoreCase)
                .Select(zone => BuildCloudflareDomainOptionAsync(zone, validation.Accounts, apiToken, settings, connectorStatus, cancellationToken))
                .ToArray();
            var domains = await Task.WhenAll(domainTasks);

            return new EdgeGatewayCloudflareStatus(
                validation.HasSavedToken,
                true,
                "Cloudflare domains loaded from the saved Edge Gateway token.",
                domains);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var message = exception.Message.Contains("Cloudflare API token", StringComparison.OrdinalIgnoreCase)
                ? "No saved Cloudflare token is registered yet. Validate a token in Edge Gateway Setup first."
                : $"Cloudflare domains could not be loaded: {exception.Message}";
            return new EdgeGatewayCloudflareStatus(false, false, message, []);
        }
    }

    private async Task<EdgeGatewayRuntimeStatus> BuildRuntimeStatusAsync(
        Task<CloudflaredConnectorStatus?> connectorStatusTask,
        CancellationToken cancellationToken)
    {
        var caddyStatusTask = BuildCaddyRuntimeStatusAsync(cancellationToken);
        var cloudflaredStatusTask = BuildCloudflaredRuntimeStatusAsync(connectorStatusTask);

        await Task.WhenAll(caddyStatusTask, cloudflaredStatusTask);
        var caddyStatus = await caddyStatusTask;
        var cloudflaredStatus = await cloudflaredStatusTask;
        var canAttemptPublish = caddyStatus.Status != EdgeGatewayDiagnosticStatus.Broken &&
                                cloudflaredStatus.Status != EdgeGatewayDiagnosticStatus.Broken;
        var summary = caddyStatus.Status == EdgeGatewayDiagnosticStatus.Good &&
                      cloudflaredStatus.Status == EdgeGatewayDiagnosticStatus.Good
            ? "Caddy and cloudflared are ready for Edge Gateway traffic."
            : "Relay setup or publish will try to prepare missing local edge services.";

        return new EdgeGatewayRuntimeStatus(caddyStatus, cloudflaredStatus, canAttemptPublish, summary);
    }

    private async Task<EdgeGatewayRuntimeComponentStatus> BuildCaddyRuntimeStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var caddy = await caddyIntegrationService.GetDashboardAsync(cancellationToken);
            if (!caddy.IsInstalled)
            {
                return new EdgeGatewayRuntimeComponentStatus(
                    EdgeGatewayDiagnosticStatus.NotConfigured,
                    "Caddy",
                    "Caddy is not installed yet. Edge Gateway setup will install and start it before publishing.");
            }

            if (!caddy.IsConfigurationValid)
            {
                return new EdgeGatewayRuntimeComponentStatus(
                    EdgeGatewayDiagnosticStatus.Broken,
                    "Caddy",
                    $"Caddy is installed, but its current configuration does not validate: {caddy.ValidationSummary}");
            }

            if (!caddy.IsServiceActive)
            {
                return new EdgeGatewayRuntimeComponentStatus(
                    EdgeGatewayDiagnosticStatus.Warning,
                    "Caddy",
                    "Caddy is installed but not running. Edge Gateway setup will restart it before publishing.");
            }

            return new EdgeGatewayRuntimeComponentStatus(
                EdgeGatewayDiagnosticStatus.Good,
                "Caddy",
                string.IsNullOrWhiteSpace(caddy.InstalledVersion)
                    ? "Caddy is installed, running, and its configuration validates."
                    : $"Caddy {caddy.InstalledVersion} is installed, running, and its configuration validates.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new EdgeGatewayRuntimeComponentStatus(
                EdgeGatewayDiagnosticStatus.Broken,
                "Caddy",
                $"Caddy status could not be checked: {exception.Message}");
        }
    }

    private static async Task<EdgeGatewayRuntimeComponentStatus> BuildCloudflaredRuntimeStatusAsync(
        Task<CloudflaredConnectorStatus?> connectorStatusTask)
    {
        var connectorStatus = await connectorStatusTask;
        if (connectorStatus is null or { IsInstalled: false })
        {
            return new EdgeGatewayRuntimeComponentStatus(
                EdgeGatewayDiagnosticStatus.NotConfigured,
                "cloudflared",
                "cloudflared is not installed as a local connector yet. Relay setup will install it for the selected tunnel.");
        }

        if (!connectorStatus.IsRunning)
        {
            return new EdgeGatewayRuntimeComponentStatus(
                EdgeGatewayDiagnosticStatus.Warning,
                "cloudflared",
                string.IsNullOrWhiteSpace(connectorStatus.TunnelId)
                    ? "cloudflared is installed but not running."
                    : $"cloudflared is installed for tunnel {connectorStatus.TunnelId}, but the service is not running.");
        }

        return new EdgeGatewayRuntimeComponentStatus(
            EdgeGatewayDiagnosticStatus.Good,
            "cloudflared",
            string.IsNullOrWhiteSpace(connectorStatus.TunnelId)
                ? "cloudflared is installed and running."
                : $"cloudflared is installed and running for tunnel {connectorStatus.TunnelId}.");
    }

    private async Task<EdgeGatewayCloudflareDomainOption> BuildCloudflareDomainOptionAsync(
        CloudflareZone zone,
        IReadOnlyList<CloudflareAccount> accounts,
        string apiToken,
        EdgeGatewaySettings settings,
        CloudflaredConnectorStatus? connectorStatus,
        CancellationToken cancellationToken)
    {
        var gatewayDomainName = BuildGatewayDomainName(zone.Name, settings.GatewaySubdomain);
        var relayDnsTarget = string.Empty;
        var relayUsesCloudflareTunnel = false;
        var relayTunnelId = string.Empty;
        var relayTunnelName = string.Empty;
        var relayOwnedByThisLms = false;
        var relayOwnershipSummary = string.Empty;
        if (!string.IsNullOrWhiteSpace(apiToken))
        {
            try
            {
                var records = await cloudflareDnsService.ListRecordsAsync(apiToken, zone.Id, cancellationToken);
                var relayRecord = records.FirstOrDefault(record => IsWildcardRecordForDomain(record, gatewayDomainName));
                relayDnsTarget = relayRecord?.Content?.Trim() ?? string.Empty;
                relayUsesCloudflareTunnel = relayRecord is not null &&
                                            relayRecord.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase) &&
                                            relayDnsTarget.EndsWith(".cfargotunnel.com", StringComparison.OrdinalIgnoreCase);
                if (relayUsesCloudflareTunnel)
                {
                    relayTunnelId = TryExtractTunnelIdFromDnsTarget(relayDnsTarget) ?? string.Empty;
                    var account = ResolveCloudflareAccount(accounts, zone);
                    if (account is null)
                    {
                        relayOwnershipSummary = "Cloudflare did not return the account that owns this zone.";
                    }
                    else if (string.IsNullOrWhiteSpace(relayTunnelId))
                    {
                        relayOwnershipSummary = $"*.{gatewayDomainName} points at a Cloudflare Tunnel target, but the tunnel ID could not be read.";
                    }
                    else
                    {
                        var tunnels = await cloudflareTunnelService.ListTunnelsAsync(apiToken, account.Id, cancellationToken);
                        var relayTunnel = tunnels.FirstOrDefault(tunnel =>
                            !tunnel.IsDeleted &&
                            tunnel.Id.Equals(relayTunnelId, StringComparison.OrdinalIgnoreCase));
                        relayTunnelName = relayTunnel?.Name ?? string.Empty;
                        relayOwnedByThisLms = IsRelayTunnelOwnedByThisLms(relayTunnel, gatewayDomainName, settings.TunnelInstanceId, connectorStatus);
                        relayOwnershipSummary = relayTunnel is null
                            ? $"Cloudflare Tunnel {relayTunnelId} was not returned by this account."
                            : relayOwnedByThisLms
                                ? IsLocalConnectorTunnel(relayTunnel, connectorStatus)
                                    ? $"Owned by this LMS host via the local cloudflared connector tunnel {relayTunnel.Name}."
                                    : $"Owned by this LMS instance via tunnel {relayTunnel.Name}."
                                : $"Points at Cloudflare Tunnel {relayTunnel.Name}, which is not owned by this LMS instance.";
                    }
                }
                else if (relayRecord is not null)
                {
                    relayOwnershipSummary = $"*.{gatewayDomainName} exists but does not point at a Cloudflare Tunnel target.";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                relayDnsTarget = string.Empty;
                relayUsesCloudflareTunnel = false;
                relayTunnelId = string.Empty;
                relayTunnelName = string.Empty;
                relayOwnedByThisLms = false;
                relayOwnershipSummary = "LMS could not inspect relay ownership for this zone.";
            }
        }

        return new EdgeGatewayCloudflareDomainOption(
            zone.Id,
            zone.Name,
            gatewayDomainName,
            zone.AccountId,
            string.IsNullOrWhiteSpace(zone.AccountName)
                ? accounts.FirstOrDefault(account => account.Id == zone.AccountId)?.Name ?? string.Empty
                : zone.AccountName,
            zone.Status,
            zone.Paused,
            false,
            !string.IsNullOrWhiteSpace(relayDnsTarget),
            relayDnsTarget,
            relayUsesCloudflareTunnel,
            relayTunnelId,
            relayTunnelName,
            relayOwnedByThisLms,
            relayOwnershipSummary);
    }

    public async Task<EdgeGatewayCloudflareSetupResult> ProvisionCloudflareDomainAsync(
        string domainName,
        bool replaceExistingDnsRecord,
        CancellationToken cancellationToken = default)
    {
        var normalizedDomain = EdgeGatewayRouteValidator.NormalizeDomainName(domainName);
        var gatewaySettings = await GetGatewaySettingsAsync(cancellationToken);
        var gatewaySubdomain = gatewaySettings.GatewaySubdomain;
        var steps = new List<string>();
        var warnings = new List<string>();
        var apiToken = await ResolveSavedCloudflareTokenAsync(cancellationToken);
        var validation = await exposedServiceManager.ValidateTokenAsync(AiLocalMachine.ManagedHostId, null, cancellationToken);
        var zone = validation.Zones.FirstOrDefault(zone =>
            zone.Name.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase));
        if (zone is null)
        {
            throw new InvalidOperationException($"The saved Cloudflare token cannot manage {normalizedDomain}.");
        }

        var account = ResolveCloudflareAccount(validation.Accounts, zone);
        if (account is null)
        {
            throw new InvalidOperationException($"Cloudflare did not return an account that owns {normalizedDomain}.");
        }

        steps.Add($"Cloudflare zone found: {normalizedDomain} in {account.Name}.");

        var gatewayDomainName = BuildGatewayDomainName(normalizedDomain, gatewaySubdomain);
        steps.Add($"Edge Gateway namespace: {gatewayDomainName}.");

        var wildcardHostname = $"*.{gatewayDomainName}";
        var caddyServiceUrl = ResolveCaddyServiceUrl();
        var caddyReady = await EnsureCaddyReadyForPublishingAsync(steps, warnings, cancellationToken);
        if (!caddyReady.Succeeded)
        {
            return new EdgeGatewayCloudflareSetupResult(
                false,
                false,
                false,
                normalizedDomain,
                gatewayDomainName,
                string.Empty,
                string.Empty,
                string.Empty,
                wildcardHostname,
                caddyServiceUrl,
                caddyReady.Summary,
                steps,
                warnings,
                string.Empty);
        }

        var existingRecords = await cloudflareDnsService.ListRecordsAsync(apiToken, zone.Id, cancellationToken);
        var existingWildcardRecord = existingRecords.FirstOrDefault(record => IsWildcardRecordForDomain(record, gatewayDomainName));
        var tunnels = await cloudflareTunnelService.ListTunnelsAsync(apiToken, account.Id, cancellationToken);
        var connectorStatus = await TryInspectLocalCloudflaredConnectorAsync(cancellationToken);
        var tunnelBaseName = BuildEdgeTunnelBaseName(gatewayDomainName, gatewaySettings.TunnelInstanceId);
        var tunnelName = BuildUniqueEdgeTunnelName(tunnelBaseName, tunnels);
        var connectorTunnel = ResolveConnectorTunnel(tunnels, connectorStatus);
        var tunnel = ResolveDesiredEdgeGatewayTunnel(tunnels, connectorTunnel, tunnelBaseName);
        if (tunnel is not null)
        {
            steps.Add(IsLocalConnectorTunnel(tunnel, connectorStatus)
                ? connectorStatus?.IsRunning == true
                    ? $"Local cloudflared already uses the LMS Edge Gateway tunnel {tunnel.Name}."
                    : $"Local cloudflared already uses the LMS Edge Gateway tunnel {tunnel.Name}, but the service is not active."
                : $"Reused Cloudflare Tunnel {tunnel.Name} for this LMS relay namespace.");
        }

        if (tunnel is null)
        {
            tunnel = await cloudflareTunnelService.CreateTunnelAsync(apiToken, account.Id, tunnelName, cancellationToken);
            steps.Add($"Created Cloudflare Tunnel {tunnel.Name}.");
        }

        var replaceExistingConnector = ShouldReplaceLocalCloudflaredConnector(connectorStatus, tunnel.Id);
        if (replaceExistingConnector)
        {
            steps.Add(BuildCloudflaredReplacementStep(connectorStatus, tunnel.Name));
        }

        var dnsTarget = $"{tunnel.Id}.cfargotunnel.com";
        if (existingWildcardRecord is null)
        {
            await cloudflareDnsService.CreateRecordAsync(
                apiToken,
                zone.Id,
                new CloudflareDnsRecord(
                    string.Empty,
                    zone.Id,
                    wildcardHostname,
                    "CNAME",
                    dnsTarget,
                    true,
                    1,
                    cloudflareOptions.ManagedRecordComment,
                    null),
                cancellationToken);
            steps.Add($"Created proxied wildcard DNS record {wildcardHostname} -> {dnsTarget}.");
        }
        else if (IsSameDnsTarget(existingWildcardRecord, dnsTarget))
        {
            steps.Add($"Wildcard DNS record already points at {dnsTarget}.");
        }
        else if (!replaceExistingDnsRecord)
        {
            return new EdgeGatewayCloudflareSetupResult(
                false,
                true,
                false,
                normalizedDomain,
                gatewayDomainName,
                tunnel.Id,
                tunnel.Name,
                dnsTarget,
                wildcardHostname,
                caddyServiceUrl,
                $"Wildcard DNS already exists for {gatewayDomainName} and points at {existingWildcardRecord.Content}. Confirm replacement to point it at the LMS Edge Gateway tunnel.",
                steps,
                [$"Existing wildcard DNS record: {existingWildcardRecord.Type} {existingWildcardRecord.Name} -> {existingWildcardRecord.Content}."],
                string.Empty);
        }
        else
        {
            await cloudflareDnsService.DeleteRecordAsync(apiToken, zone.Id, existingWildcardRecord.Id, cancellationToken);
            await cloudflareDnsService.CreateRecordAsync(
                apiToken,
                zone.Id,
                new CloudflareDnsRecord(
                    string.Empty,
                    zone.Id,
                    wildcardHostname,
                    "CNAME",
                    dnsTarget,
                    true,
                    1,
                    cloudflareOptions.ManagedRecordComment,
                    null),
                cancellationToken);
            steps.Add($"Replaced wildcard DNS record with {wildcardHostname} -> {dnsTarget}.");
        }

        var configuration = await cloudflareTunnelService.GetConfigurationAsync(apiToken, account.Id, tunnel.Id, cancellationToken);
        await cloudflareTunnelService.UpdateConfigurationAsync(
            apiToken,
            account.Id,
            tunnel.Id,
            new CloudflareTunnelConfiguration(MergeWildcardTunnelRoute(configuration.Routes, wildcardHostname, caddyServiceUrl)),
            cancellationToken);
        steps.Add($"Configured tunnel ingress {wildcardHostname} -> {caddyServiceUrl}.");

        var connectorResult = await EnsureLocalCloudflaredConnectorAsync(
            apiToken,
            account.Id,
            tunnel.Id,
            tunnel.Name,
            connectorStatus,
            replaceExistingConnector,
            cancellationToken);
        if (!connectorResult.Succeeded)
        {
            warnings.Add(connectorResult.Summary);
        }

        return new EdgeGatewayCloudflareSetupResult(
            connectorResult.Succeeded,
            false,
            connectorResult.Succeeded,
            normalizedDomain,
            gatewayDomainName,
            tunnel.Id,
            tunnel.Name,
            dnsTarget,
            wildcardHostname,
            caddyServiceUrl,
            connectorResult.Succeeded
                ? $"Cloudflare tunnel, scoped wildcard DNS, ingress, and local connector are configured for {gatewayDomainName}."
                : $"Cloudflare tunnel, scoped wildcard DNS, and ingress are configured for {gatewayDomainName}. The local cloudflared connector still needs attention.",
            steps,
            warnings,
            connectorResult.Summary);
    }

    public async Task<EdgeGatewayCloudflareRelayRemovalResult> RemoveCloudflareDomainRelayAsync(
        string domainName,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        var normalizedDomain = EdgeGatewayRouteValidator.NormalizeDomainName(domainName);
        var gatewaySettings = await GetGatewaySettingsAsync(cancellationToken);
        var gatewaySubdomain = gatewaySettings.GatewaySubdomain;
        var gatewayDomainName = BuildGatewayDomainName(normalizedDomain, gatewaySubdomain);
        var wildcardHostname = $"*.{gatewayDomainName}";
        var steps = new List<string>();
        var warnings = new List<string>();
        if (!confirmed)
        {
            return new EdgeGatewayCloudflareRelayRemovalResult(
                false,
                true,
                normalizedDomain,
                gatewayDomainName,
                wildcardHostname,
                $"Confirm removal before deleting the Edge Gateway relay for {gatewayDomainName}.",
                steps,
                warnings);
        }

        var apiToken = await ResolveSavedCloudflareTokenAsync(cancellationToken);
        var validation = await exposedServiceManager.ValidateTokenAsync(AiLocalMachine.ManagedHostId, null, cancellationToken);
        var zone = validation.Zones.FirstOrDefault(zone =>
            zone.Name.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase));
        if (zone is null)
        {
            throw new InvalidOperationException($"The saved Cloudflare token cannot manage {normalizedDomain}.");
        }

        var account = ResolveCloudflareAccount(validation.Accounts, zone);
        if (account is null)
        {
            throw new InvalidOperationException($"Cloudflare did not return an account that owns {normalizedDomain}.");
        }

        var records = await cloudflareDnsService.ListRecordsAsync(apiToken, zone.Id, cancellationToken);
        var relayRecord = records.FirstOrDefault(record => IsWildcardRecordForDomain(record, gatewayDomainName));
        var tunnelId = TryExtractTunnelIdFromDnsTarget(relayRecord?.Content);
        if (relayRecord is null)
        {
            steps.Add($"No scoped wildcard DNS record was found for {wildcardHostname}.");
            return new EdgeGatewayCloudflareRelayRemovalResult(
                true,
                false,
                normalizedDomain,
                gatewayDomainName,
                wildcardHostname,
                $"No Edge Gateway relay DNS record exists for {gatewayDomainName}.",
                steps,
                warnings);
        }

        var tunnels = await cloudflareTunnelService.ListTunnelsAsync(apiToken, account.Id, cancellationToken);
        var tunnel = string.IsNullOrWhiteSpace(tunnelId)
            ? null
            : tunnels.FirstOrDefault(item => item.Id.Equals(tunnelId, StringComparison.OrdinalIgnoreCase));
        var connectorStatus = await TryInspectLocalCloudflaredConnectorAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(tunnelId) &&
            !IsRelayTunnelOwnedByThisLms(tunnel, gatewayDomainName, gatewaySettings.TunnelInstanceId, connectorStatus))
        {
            warnings.Add(tunnel is null
                ? $"Cloudflare Tunnel {tunnelId} was not returned by the account."
                : $"Cloudflare Tunnel {tunnel.Name} ({tunnel.Id}) is not owned by this LMS instance.");
            warnings.Add($"Existing scoped wildcard DNS record: {relayRecord.Type} {relayRecord.Name} -> {relayRecord.Content}.");
        }
        else if (string.IsNullOrWhiteSpace(tunnelId))
        {
            warnings.Add($"Existing scoped wildcard DNS record does not point at a Cloudflare Tunnel target: {relayRecord.Type} {relayRecord.Name} -> {relayRecord.Content}.");
        }

        await cloudflareDnsService.DeleteRecordAsync(apiToken, zone.Id, relayRecord.Id, cancellationToken);
        steps.Add($"Deleted scoped wildcard DNS record {relayRecord.Name} -> {relayRecord.Content}.");

        if (tunnel is null)
        {
            warnings.Add("LMS could not remove tunnel ingress because the relay tunnel could not be identified from the DNS record.");
        }
        else
        {
            var configuration = await cloudflareTunnelService.GetConfigurationAsync(apiToken, account.Id, tunnel.Id, cancellationToken);
            var updatedRoutes = RemoveWildcardTunnelRoute(configuration.Routes, wildcardHostname);
            if (updatedRoutes.Count == configuration.Routes.Count)
            {
                steps.Add($"No tunnel ingress matched {wildcardHostname}.");
            }
            else
            {
                await cloudflareTunnelService.UpdateConfigurationAsync(
                    apiToken,
                    account.Id,
                    tunnel.Id,
                    new CloudflareTunnelConfiguration(updatedRoutes),
                    cancellationToken);
                steps.Add($"Removed tunnel ingress for {wildcardHostname} from {tunnel.Name}.");
            }
        }

        return new EdgeGatewayCloudflareRelayRemovalResult(
            true,
            false,
            normalizedDomain,
            gatewayDomainName,
            wildcardHostname,
            $"Removed the Edge Gateway relay for {gatewayDomainName}. Existing app routes under this domain may stop resolving until a relay is set up again.",
            steps,
            warnings);
    }

    public async Task<EdgeGatewayCloudflareRouteSetupResult> ProvisionCloudflareRouteAsync(
        Guid routeId,
        bool replaceExistingDnsRecord,
        CancellationToken cancellationToken = default)
    {
        var route = await store.GetRouteAsync(routeId, cancellationToken)
            ?? throw new InvalidOperationException("The Edge Gateway route no longer exists.");
        EdgeGatewayRouteValidator.ValidateRoute(route);

        var normalizedDomain = EdgeGatewayRouteValidator.NormalizeDomainName(route.DomainName);
        var gatewaySettings = await GetGatewaySettingsAsync(cancellationToken);
        var gatewaySubdomain = gatewaySettings.GatewaySubdomain;
        var normalizedHostname = EdgeGatewayRouteValidator.NormalizeHostname(route.Hostname);
        var relativeHostname = ResolveRelativeHostname(normalizedHostname, normalizedDomain);
        var gatewayDomainName = BuildGatewayDomainName(normalizedDomain, gatewaySubdomain);
        var relayHostname = $"{relativeHostname}.{gatewayDomainName}";
        var steps = new List<string>();
        var warnings = new List<string>();

        var caddyReady = await EnsureCaddyReadyForPublishingAsync(steps, warnings, cancellationToken);
        if (!caddyReady.Succeeded)
        {
            return new EdgeGatewayCloudflareRouteSetupResult(
                false,
                false,
                route.Id,
                normalizedHostname,
                relayHostname,
                string.Empty,
                string.Empty,
                caddyReady.Summary,
                steps,
                warnings);
        }

        var apiToken = await ResolveSavedCloudflareTokenAsync(cancellationToken);
        var validation = await exposedServiceManager.ValidateTokenAsync(AiLocalMachine.ManagedHostId, null, cancellationToken);
        var zone = validation.Zones.FirstOrDefault(zone =>
            zone.Name.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase));
        if (zone is null)
        {
            throw new InvalidOperationException($"The saved Cloudflare token cannot manage {normalizedDomain}.");
        }

        var account = ResolveCloudflareAccount(validation.Accounts, zone);
        if (account is null)
        {
            throw new InvalidOperationException($"Cloudflare did not return an account that owns {normalizedDomain}.");
        }

        var gatewaySetup = await EnsureGatewayTunnelAsync(
            apiToken,
            account,
            zone,
            normalizedDomain,
            gatewaySubdomain,
            gatewaySettings.TunnelInstanceId,
            replaceExistingDnsRecord,
            installConnector: true,
            cancellationToken);
        steps.AddRange(gatewaySetup.Steps);
        warnings.AddRange(gatewaySetup.Warnings);

        if (gatewaySetup.RequiresDnsReplacement)
        {
            return new EdgeGatewayCloudflareRouteSetupResult(
                false,
                true,
                route.Id,
                normalizedHostname,
                relayHostname,
                gatewaySetup.DnsTarget,
                gatewaySetup.Tunnel.Name,
                gatewaySetup.Summary,
                steps,
                warnings);
        }

        if (!gatewaySetup.Success)
        {
            return new EdgeGatewayCloudflareRouteSetupResult(
                false,
                false,
                route.Id,
                normalizedHostname,
                relayHostname,
                gatewaySetup.DnsTarget,
                gatewaySetup.Tunnel.Name,
                gatewaySetup.Summary,
                steps,
                warnings);
        }

        var existingRecords = await cloudflareDnsService.ListRecordsAsync(apiToken, zone.Id, cancellationToken);
        var existingRouteRecord = existingRecords.FirstOrDefault(record =>
            record.Name.Trim().TrimEnd('.').Equals(normalizedHostname, StringComparison.OrdinalIgnoreCase));

        if (existingRouteRecord is null)
        {
            await cloudflareDnsService.CreateRecordAsync(
                apiToken,
                zone.Id,
                new CloudflareDnsRecord(
                    string.Empty,
                    zone.Id,
                    normalizedHostname,
                    "CNAME",
                    relayHostname,
                    true,
                    1,
                    cloudflareOptions.ManagedRecordComment,
                    null),
                cancellationToken);
            steps.Add($"Created proxied route DNS record {normalizedHostname} -> {relayHostname}.");
        }
        else if (IsSameDnsTarget(existingRouteRecord, relayHostname))
        {
            steps.Add($"Route DNS record already points at {relayHostname}.");
        }
        else if (!replaceExistingDnsRecord)
        {
            return new EdgeGatewayCloudflareRouteSetupResult(
                false,
                true,
                route.Id,
                normalizedHostname,
                relayHostname,
                gatewaySetup.DnsTarget,
                gatewaySetup.Tunnel.Name,
                $"DNS already exists for {normalizedHostname} and points at {existingRouteRecord.Content}. Confirm replacement to point it at {relayHostname}.",
                steps,
                [$"Existing route DNS record: {existingRouteRecord.Type} {existingRouteRecord.Name} -> {existingRouteRecord.Content}."]);
        }
        else
        {
            await cloudflareDnsService.DeleteRecordAsync(apiToken, zone.Id, existingRouteRecord.Id, cancellationToken);
            await cloudflareDnsService.CreateRecordAsync(
                apiToken,
                zone.Id,
                new CloudflareDnsRecord(
                    string.Empty,
                    zone.Id,
                    normalizedHostname,
                    "CNAME",
                    relayHostname,
                    true,
                    1,
                    cloudflareOptions.ManagedRecordComment,
                    null),
                cancellationToken);
            steps.Add($"Replaced route DNS record with {normalizedHostname} -> {relayHostname}.");
        }

        var configuration = await cloudflareTunnelService.GetConfigurationAsync(
            apiToken,
            account.Id,
            gatewaySetup.Tunnel.Id,
            cancellationToken);
        await cloudflareTunnelService.UpdateConfigurationAsync(
            apiToken,
            account.Id,
            gatewaySetup.Tunnel.Id,
            new CloudflareTunnelConfiguration(MergeHostnameTunnelRoute(configuration.Routes, normalizedHostname, ResolveCaddyServiceUrl())),
            cancellationToken);
        steps.Add($"Configured tunnel ingress {normalizedHostname} -> {ResolveCaddyServiceUrl()}.");

        var caddyApplyResult = await ApplyCaddyConfigurationAsync(cancellationToken);
        if (caddyApplyResult.Success)
        {
            steps.Add("Applied generated Caddy configuration for the route.");
        }
        else
        {
            warnings.Add($"Cloudflare was updated, but Caddy config was not applied: {caddyApplyResult.Summary}");
            return new EdgeGatewayCloudflareRouteSetupResult(
                false,
                false,
                route.Id,
                normalizedHostname,
                relayHostname,
                gatewaySetup.DnsTarget,
                gatewaySetup.Tunnel.Name,
                $"Cloudflare was updated, but Caddy did not reload: {caddyApplyResult.Summary}",
                steps,
                warnings);
        }

        return new EdgeGatewayCloudflareRouteSetupResult(
            true,
            false,
            route.Id,
            normalizedHostname,
            relayHostname,
            gatewaySetup.DnsTarget,
            gatewaySetup.Tunnel.Name,
            $"{normalizedHostname} now points through {relayHostname} to the LMS Edge Gateway tunnel.",
            steps,
            warnings);
    }

    public async Task<EdgeGatewayRemoteLmsRelaySetupResult> ProvisionRemoteLmsRelayAsync(
        string domainName,
        string hostname,
        CancellationToken cancellationToken = default)
    {
        var normalizedDomain = EdgeGatewayRouteValidator.NormalizeDomainName(domainName);
        var normalizedHostname = EdgeGatewayRouteValidator.NormalizeHostname(hostname);
        _ = ResolveRelativeHostname(normalizedHostname, normalizedDomain);
        var gatewaySettings = await GetGatewaySettingsAsync(cancellationToken);
        var gatewaySubdomain = gatewaySettings.GatewaySubdomain;
        var steps = new List<string>();
        var warnings = new List<string>();
        var apiToken = await ResolveSavedCloudflareTokenAsync(cancellationToken);
        var validation = await exposedServiceManager.ValidateTokenAsync(AiLocalMachine.ManagedHostId, null, cancellationToken);
        var zone = validation.Zones.FirstOrDefault(zone =>
            zone.Name.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase));
        if (zone is null)
        {
            throw new InvalidOperationException($"The saved Cloudflare token cannot manage {normalizedDomain}.");
        }

        var account = ResolveCloudflareAccount(validation.Accounts, zone);
        if (account is null)
        {
            throw new InvalidOperationException($"Cloudflare did not return an account that owns {normalizedDomain}.");
        }

        var gatewaySetup = await EnsureGatewayTunnelAsync(
            apiToken,
            account,
            zone,
            normalizedDomain,
            gatewaySubdomain,
            gatewaySettings.TunnelInstanceId,
            replaceExistingDnsRecord: false,
            installConnector: true,
            cancellationToken);
        steps.AddRange(gatewaySetup.Steps);
        warnings.AddRange(gatewaySetup.Warnings);

        if (!gatewaySetup.Success || gatewaySetup.RequiresDnsReplacement)
        {
            return new EdgeGatewayRemoteLmsRelaySetupResult(
                false,
                normalizedDomain,
                normalizedHostname,
                gatewaySetup.DnsTarget,
                gatewaySetup.Tunnel.Name,
                gatewaySetup.Summary,
                steps,
                warnings);
        }

        var existingRecords = await cloudflareDnsService.ListRecordsAsync(apiToken, zone.Id, cancellationToken);
        var existingRecord = existingRecords.FirstOrDefault(record =>
            record.Name.Trim().TrimEnd('.').Equals(normalizedHostname, StringComparison.OrdinalIgnoreCase));

        if (existingRecord is null)
        {
            await cloudflareDnsService.CreateRecordAsync(
                apiToken,
                zone.Id,
                new CloudflareDnsRecord(
                    string.Empty,
                    zone.Id,
                    normalizedHostname,
                    "CNAME",
                    gatewaySetup.DnsTarget,
                    true,
                    1,
                    cloudflareOptions.ManagedRecordComment,
                    null),
                cancellationToken);
            steps.Add($"Created proxied remote LMS DNS record {normalizedHostname} -> {gatewaySetup.DnsTarget}.");
        }
        else if (IsSameDnsTarget(existingRecord, gatewaySetup.DnsTarget))
        {
            steps.Add($"Remote LMS DNS record already points at {gatewaySetup.DnsTarget}.");
        }
        else
        {
            return new EdgeGatewayRemoteLmsRelaySetupResult(
                false,
                normalizedDomain,
                normalizedHostname,
                gatewaySetup.DnsTarget,
                gatewaySetup.Tunnel.Name,
                $"DNS already exists for {normalizedHostname} and points at {existingRecord.Content}. Choose a different host name or remove the conflicting DNS record.",
                steps,
                [$"Existing DNS record: {existingRecord.Type} {existingRecord.Name} -> {existingRecord.Content}."]);
        }

        var caddyServiceUrl = ResolveCaddyServiceUrl();
        var configuration = await cloudflareTunnelService.GetConfigurationAsync(
            apiToken,
            account.Id,
            gatewaySetup.Tunnel.Id,
            cancellationToken);
        await cloudflareTunnelService.UpdateConfigurationAsync(
            apiToken,
            account.Id,
            gatewaySetup.Tunnel.Id,
            new CloudflareTunnelConfiguration(MergeHostnameTunnelRoute(configuration.Routes, normalizedHostname, caddyServiceUrl)),
            cancellationToken);
        steps.Add($"Configured tunnel ingress {normalizedHostname} -> {caddyServiceUrl}.");

        return new EdgeGatewayRemoteLmsRelaySetupResult(
            true,
            normalizedDomain,
            normalizedHostname,
            gatewaySetup.DnsTarget,
            gatewaySetup.Tunnel.Name,
            $"{normalizedHostname} is ready for remote LMS SSH relay traffic.",
            steps,
            warnings);
    }

    public Task DeleteRouteAsync(Guid routeId, CancellationToken cancellationToken = default) =>
        store.DeleteRouteAsync(routeId, cancellationToken);

    public async Task<EdgeGatewayDiagnosticResult> TestRouteAsync(Guid routeId, CancellationToken cancellationToken = default)
    {
        var route = await store.GetRouteAsync(routeId, cancellationToken);
        if (route is null)
        {
            return new EdgeGatewayDiagnosticResult(EdgeGatewayDiagnosticStatus.Broken, "Route no longer exists.", []);
        }

        var checks = new List<string>();
        try
        {
            EdgeGatewayRouteValidator.ValidateRoute(route);
            checks.Add("Route fields are valid.");
        }
        catch (Exception exception)
        {
            var brokenRoute = route with
            {
                LastTestStatus = EdgeGatewayDiagnosticStatus.Broken,
                LastTestMessage = exception.Message,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await store.SaveRouteAsync(brokenRoute, cancellationToken);
            return new EdgeGatewayDiagnosticResult(EdgeGatewayDiagnosticStatus.Broken, exception.Message, [exception.Message]);
        }

        var targetUrl = EdgeGatewayRouteValidator.BuildTargetUrl(route);
        try
        {
            checks.Add(await ProbeBackendAsync(targetUrl, cancellationToken));
        }
        catch (Exception exception)
        {
            checks.Add($"Backend check failed: {exception.Message}");
            var updatedRoute = route with
            {
                LastTestStatus = EdgeGatewayDiagnosticStatus.Broken,
                LastTestMessage = "Backend target was not reachable from this LMS host.",
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await store.SaveRouteAsync(updatedRoute, cancellationToken);
            return new EdgeGatewayDiagnosticResult(updatedRoute.LastTestStatus, updatedRoute.LastTestMessage, checks);
        }

        var generated = caddyfileGenerator.Generate([route]);
        var validateResult = await caddyManager.ValidateAsync(generated, cancellationToken);
        checks.Add(validateResult.Success ? "Generated Caddy config validates." : validateResult.Summary);

        var status = validateResult.Success ? EdgeGatewayDiagnosticStatus.Good : EdgeGatewayDiagnosticStatus.Broken;
        var message = validateResult.Success
            ? "Route check passed."
            : validateResult.Summary;
        await store.SaveRouteAsync(route with
        {
            LastTestStatus = status,
            LastTestMessage = message,
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);

        return new EdgeGatewayDiagnosticResult(status, message, checks);
    }

    public async Task<EdgeGatewayCaddyApplyResult> ApplyCaddyConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var routes = await store.ListRoutesAsync(cancellationToken);
        return await caddyManager.ApplyAsync(caddyfileGenerator.Generate(routes), cancellationToken);
    }

    public Task<EdgeGatewayCaddyApplyResult> RollbackCaddyConfigurationAsync(CancellationToken cancellationToken = default) =>
        caddyManager.RollbackAsync(cancellationToken);

    public async Task<EdgeGatewayCaddyApplyResult> PanicDisableAllRoutesAsync(CancellationToken cancellationToken = default)
    {
        await store.DisableAllRoutesAsync(DateTimeOffset.UtcNow, cancellationToken);
        var routes = await store.ListRoutesAsync(cancellationToken);
        return await caddyManager.ApplyAsync(caddyfileGenerator.Generate(routes), cancellationToken);
    }

    public Task<IReadOnlyList<EdgeGatewayAuditEntry>> ListAuditEntriesAsync(
        EdgeGatewayAuditFilter filter,
        CancellationToken cancellationToken = default) =>
        store.ListAuditEntriesAsync(
            filter.Hostname,
            filter.UserEmail,
            filter.Decision,
            filter.FromUtc,
            filter.ToUtc,
            Math.Clamp(filter.Take, 1, 1000),
            cancellationToken);

    public async Task<EdgeGatewayAuthCheckResult> EvaluateAuthAsync(
        EdgeGatewayAuthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var requestedHost = NormalizeForwardedHost(context.ForwardedHost, context.Host);
        var requestedPath = string.IsNullOrWhiteSpace(context.ForwardedUri) ? "/" : context.ForwardedUri.Trim();
        var sourceIp = ResolveSourceIp(context.ForwardedFor, context.RemoteIpAddress);
        var userEmail = FindFirstValue(context.User, ClaimTypes.Email) ?? FindFirstValue(context.User, ClaimTypes.Name) ?? string.Empty;

        if (!IsTrustedAuthProxy(context.RemoteIpAddress))
        {
            return await AuditAndReturnAsync(
                null,
                requestedHost,
                requestedPath,
                sourceIp,
                userEmail,
                EdgeGatewayDecision.Denied,
                StatusForbidden,
                "Forward auth request did not come from a trusted local proxy.",
                EdgeGatewayAuthMode.Blocked,
                cancellationToken);
        }

        var route = await FindRouteForRequestAsync(requestedHost, requestedPath, enabledOnly: true, cancellationToken);
        if (route is null || !route.Enabled)
        {
            return await AuditAndReturnAsync(
                route,
                requestedHost,
                requestedPath,
                sourceIp,
                userEmail,
                EdgeGatewayDecision.Denied,
                StatusNotFound,
                "No enabled Edge Gateway route matched the requested hostname.",
                route?.AuthMode ?? EdgeGatewayAuthMode.Blocked,
                cancellationToken);
        }

        route = route with { AuthMode = NormalizePublicAuthMode(route.AuthMode) };

        if (route.AuthMode == EdgeGatewayAuthMode.PassThrough)
        {
            return await AllowAsync(route, requestedHost, requestedPath, sourceIp, userEmail, context.User, "Pass-through route.", cancellationToken);
        }

        if (route.AuthMode == EdgeGatewayAuthMode.Blocked)
        {
            return await AuditAndReturnAsync(
                route,
                requestedHost,
                requestedPath,
                sourceIp,
                userEmail,
                EdgeGatewayDecision.Blocked,
                StatusForbidden,
                "Route is blocked.",
                route.AuthMode,
                cancellationToken);
        }

        if (route.AllowLanOnly && !IsLanAddress(sourceIp))
        {
            return await AuditAndReturnAsync(
                route,
                requestedHost,
                requestedPath,
                sourceIp,
                userEmail,
                EdgeGatewayDecision.Denied,
                StatusForbidden,
                "Route only allows LAN source addresses.",
                route.AuthMode,
                cancellationToken);
        }

        if (!IsKnownIpAllowed(route, sourceIp))
        {
            return await AuditAndReturnAsync(
                route,
                requestedHost,
                requestedPath,
                sourceIp,
                userEmail,
                EdgeGatewayDecision.Denied,
                StatusForbidden,
                "Source IP did not match the route allow-list.",
                route.AuthMode,
                cancellationToken);
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            return await RedirectToLoginAsync(route, requestedHost, requestedPath, sourceIp, "MFA/passkey required.", cancellationToken);
        }

        if (!IsUserAllowed(route, userEmail))
        {
            return await AuditAndReturnAsync(
                route,
                requestedHost,
                requestedPath,
                sourceIp,
                userEmail,
                EdgeGatewayDecision.Denied,
                StatusForbidden,
                "Signed-in user is not in the route allow-list.",
                route.AuthMode,
                cancellationToken);
        }

        if (!IsGroupAllowed(route, context.User))
        {
            return await AuditAndReturnAsync(
                route,
                requestedHost,
                requestedPath,
                sourceIp,
                userEmail,
                EdgeGatewayDecision.Denied,
                StatusForbidden,
                "Signed-in user is not in an allowed group.",
                route.AuthMode,
                cancellationToken);
        }

        if (!IsMfaOrPasskeySatisfied(context.User))
        {
            return await RedirectToLoginAsync(route, requestedHost, requestedPath, sourceIp, "MFA/passkey required.", cancellationToken);
        }

        return await AllowAsync(route, requestedHost, requestedPath, sourceIp, userEmail, context.User, "Policy allowed.", cancellationToken);
    }

    public async Task<string> BuildSafeReturnPathAsync(string targetUrl, CancellationToken cancellationToken = default)
    {
        if (!await IsSafeReturnTargetAsync(targetUrl, cancellationToken))
        {
            return "/";
        }

        return $"/edge-auth/return?target={Uri.EscapeDataString(targetUrl.Trim())}";
    }

    public async Task<bool> IsSafeReturnTargetAsync(string targetUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(targetUrl?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        if (IsEdgeAuthenticationPath(uri.AbsolutePath))
        {
            return false;
        }

        var route = await FindRouteForRequestAsync(uri.Host, uri.AbsolutePath, enabledOnly: true, cancellationToken);
        return route is { Enabled: true };
    }

    private async Task<EdgeGatewayAuthCheckResult> AllowAsync(
        EdgeGatewayRoute route,
        string requestedHost,
        string requestedPath,
        string sourceIp,
        string userEmail,
        ClaimsPrincipal user,
        string reason,
        CancellationToken cancellationToken)
    {
        var groups = string.Join(",", ResolveGroups(user));
        await AddAuditAsync(route, requestedHost, requestedPath, sourceIp, userEmail, EdgeGatewayDecision.Allowed, reason, route.AuthMode, cancellationToken);
        return new EdgeGatewayAuthCheckResult(
            StatusOk,
            EdgeGatewayDecision.Allowed,
            reason,
            UserName: FindFirstValue(user, ClaimTypes.Name) ?? userEmail,
            UserEmail: userEmail,
            Groups: groups);
    }

    private async Task<EdgeGatewayAuthCheckResult> RedirectToLoginAsync(
        EdgeGatewayRoute route,
        string requestedHost,
        string requestedPath,
        string sourceIp,
        string reason,
        CancellationToken cancellationToken)
    {
        var targetPath = IsEdgeAuthenticationPath(requestedPath) ? "/" : requestedPath;
        var targetUrl = BuildRequestedUrl(requestedHost, targetPath);
        var safeReturnPath = await BuildSafeReturnPathAsync(targetUrl, cancellationToken);
        var loginPath = $"/login?returnUrl={Uri.EscapeDataString(safeReturnPath)}&error={Uri.EscapeDataString(reason)}";
        var location = string.IsNullOrWhiteSpace(options.PublicLoginBaseUrl)
            ? loginPath
            : $"{options.PublicLoginBaseUrl.TrimEnd('/')}{loginPath}";

        await AddAuditAsync(route, requestedHost, requestedPath, sourceIp, string.Empty, EdgeGatewayDecision.Redirect, reason, route.AuthMode, cancellationToken);
        return new EdgeGatewayAuthCheckResult(
            StatusFound,
            EdgeGatewayDecision.Redirect,
            reason,
            location);
    }

    private async Task<EdgeGatewayAuthCheckResult> AuditAndReturnAsync(
        EdgeGatewayRoute? route,
        string requestedHost,
        string requestedPath,
        string sourceIp,
        string userEmail,
        EdgeGatewayDecision decision,
        int statusCode,
        string reason,
        EdgeGatewayAuthMode authMode,
        CancellationToken cancellationToken)
    {
        await AddAuditAsync(route, requestedHost, requestedPath, sourceIp, userEmail, decision, reason, authMode, cancellationToken);
        return new EdgeGatewayAuthCheckResult(statusCode, decision, reason);
    }

    private async Task<EdgeGatewayRoute?> FindRouteForRequestAsync(
        string requestedHost,
        string requestedPath,
        bool enabledOnly,
        CancellationToken cancellationToken)
    {
        var normalizedHost = NormalizeForwardedHost(requestedHost, requestedHost);
        var pathOnly = ExtractPathOnly(requestedPath);
        var routes = await store.ListRoutesAsync(cancellationToken);

        return routes
            .Where(route =>
                route.Hostname.Equals(normalizedHost, StringComparison.OrdinalIgnoreCase) &&
                (!enabledOnly || route.Enabled) &&
                RoutePathMatches(route.TargetPathPrefix, pathOnly))
            .OrderByDescending(route => EdgeGatewayRouteValidator.NormalizePathPrefix(route.TargetPathPrefix).Length)
            .FirstOrDefault();
    }

    private static bool RoutePathMatches(string? routePathPrefix, string requestedPath)
    {
        var prefix = EdgeGatewayRouteValidator.NormalizePathPrefix(routePathPrefix);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return true;
        }

        var normalizedPath = ExtractPathOnly(requestedPath);
        return normalizedPath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith($"{prefix}/", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractPathOnly(string? requestedPath)
    {
        var normalized = string.IsNullOrWhiteSpace(requestedPath) ? "/" : requestedPath.Trim();
        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = $"/{normalized}";
        }

        var queryIndex = normalized.IndexOf('?', StringComparison.Ordinal);
        return queryIndex < 0 ? normalized : normalized[..queryIndex];
    }

    private Task AddAuditAsync(
        EdgeGatewayRoute? route,
        string hostname,
        string requestedPath,
        string sourceIp,
        string userEmail,
        EdgeGatewayDecision decision,
        string reason,
        EdgeGatewayAuthMode authMode,
        CancellationToken cancellationToken) =>
        store.AddAuditEntryAsync(
            new EdgeGatewayAuditEntry(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                hostname,
                route?.Id,
                requestedPath,
                sourceIp,
                userEmail,
                decision,
                reason,
                authMode),
            cancellationToken);

    private bool IsTrustedAuthProxy(IPAddress? remoteIpAddress)
    {
        if (remoteIpAddress is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(remoteIpAddress))
        {
            return true;
        }

        var entries = options.TrustedProxyCidrs
            .Where(TrustedNetworkMatcher.IsValidAddressOrCidr)
            .Select(item => new TrustedNetworkEntry(Guid.NewGuid(), item, item, string.Empty, true, true, false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))
            .ToArray();
        return TrustedNetworkMatcher.Match(remoteIpAddress, entries) is not null;
    }

    private static bool IsKnownIpAllowed(EdgeGatewayRoute route, string sourceIp)
    {
        var allowed = EdgeGatewayRouteValidator.SplitList(route.AllowKnownIps);
        if (allowed.Count == 0)
        {
            return true;
        }

        if (!IPAddress.TryParse(sourceIp, out var parsed))
        {
            return false;
        }

        var entries = allowed
            .Where(TrustedNetworkMatcher.IsValidAddressOrCidr)
            .Select(item => new TrustedNetworkEntry(Guid.NewGuid(), item, item, string.Empty, true, true, false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))
            .ToArray();
        return TrustedNetworkMatcher.Match(parsed, entries) is not null;
    }

    private static bool IsLanAddress(string sourceIp)
    {
        if (!IPAddress.TryParse(sourceIp, out var address))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork ||
            address.IsIPv4MappedToIPv6)
        {
            var bytes = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).GetAddressBytes();
            return bytes[0] == 10 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 169 && bytes[1] == 254;
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
    }

    private static bool IsUserAllowed(EdgeGatewayRoute route, string userEmail)
    {
        var allowedUsers = EdgeGatewayRouteValidator.SplitList(route.AllowedUsers);
        return allowedUsers.Count == 0 ||
               allowedUsers.Contains(userEmail, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsGroupAllowed(EdgeGatewayRoute route, ClaimsPrincipal user)
    {
        var allowedGroups = EdgeGatewayRouteValidator.SplitList(route.AllowedGroups);
        if (allowedGroups.Count == 0)
        {
            return true;
        }

        var groups = ResolveGroups(user);
        return allowedGroups.Intersect(groups, StringComparer.OrdinalIgnoreCase).Any();
    }

    private static IReadOnlyList<string> ResolveGroups(ClaimsPrincipal user) =>
        user.Claims
            .Where(static claim => claim.Type is ClaimTypes.GroupSid or ClaimTypes.Role or "groups" or "lms:group")
            .Select(static claim => claim.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsMfaSatisfied(ClaimsPrincipal user) =>
        user.HasClaim("lms:mfa", "true") ||
        user.HasClaim("amr", "mfa") ||
        user.HasClaim("amr", "otp");

    private static bool IsPasskeySatisfied(ClaimsPrincipal user) =>
        user.HasClaim("lms:passkey", "true") ||
        user.HasClaim("amr", "passkey") ||
        user.HasClaim("amr", "webauthn");

    private static bool IsMfaOrPasskeySatisfied(ClaimsPrincipal user) =>
        IsMfaSatisfied(user) || IsPasskeySatisfied(user);

    private static EdgeGatewayAuthMode NormalizePublicAuthMode(EdgeGatewayAuthMode authMode) => authMode switch
    {
        EdgeGatewayAuthMode.PassThrough => EdgeGatewayAuthMode.PassThrough,
        EdgeGatewayAuthMode.Blocked => EdgeGatewayAuthMode.Blocked,
        _ => EdgeGatewayAuthMode.RequireMfa
    };

    private static string NormalizeForwardedHost(string forwardedHost, string fallbackHost)
    {
        var value = string.IsNullOrWhiteSpace(forwardedHost) ? fallbackHost : forwardedHost;
        var host = value.Split(',')[0].Trim();
        if (host.StartsWith("[", StringComparison.Ordinal))
        {
            var closingIndex = host.IndexOf(']');
            return closingIndex > 0 ? host[1..closingIndex].ToLowerInvariant() : host.ToLowerInvariant();
        }

        var colonIndex = host.IndexOf(':');
        return (colonIndex > 0 ? host[..colonIndex] : host).Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static string ResolveSourceIp(string forwardedFor, IPAddress? remoteIpAddress)
    {
        var firstForwarded = (forwardedFor ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstForwarded)
            ? remoteIpAddress?.ToString() ?? string.Empty
            : firstForwarded;
    }

    private static string BuildRequestedUrl(string hostname, string requestedPath)
    {
        var path = string.IsNullOrWhiteSpace(requestedPath) ? "/" : requestedPath;
        return $"https://{hostname}{(path.StartsWith('/') ? path : $"/{path}")}";
    }

    private static bool IsEdgeAuthenticationPath(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return false;
        }

        var path = requestedPath.Split('?', 2)[0].Trim();
        return IsPathOrChild(path, "/login") ||
               IsPathOrChild(path, "/auth") ||
               IsPathOrChild(path, "/edge-auth") ||
               IsPathOrChild(path, "/access-denied");
    }

    private static bool IsPathOrChild(string path, string prefix) =>
        path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith($"{prefix}/", StringComparison.OrdinalIgnoreCase);

    private async Task<EdgeRuntimePrepareAttempt> EnsureCaddyReadyForPublishingAsync(
        List<string> steps,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            var caddy = await caddyIntegrationService.GetDashboardAsync(cancellationToken);
            if (!caddy.IsInstalled)
            {
                steps.Add("Caddy is not installed on this LMS host; installing it before publishing.");
                var install = await caddyIntegrationService.InstallAsync(cancellationToken);
                steps.Add(install.Summary);
                if (!install.Success)
                {
                    return new EdgeRuntimePrepareAttempt(false, $"Caddy install failed: {install.Summary}");
                }

                caddy = await caddyIntegrationService.GetDashboardAsync(cancellationToken);
            }

            if (!caddy.IsServiceActive)
            {
                steps.Add("Caddy is installed but not running; restarting it before publishing.");
                var restart = await caddyIntegrationService.RestartAsync(cancellationToken);
                steps.Add(restart.Summary);
                if (!restart.Success)
                {
                    return new EdgeRuntimePrepareAttempt(false, $"Caddy could not be started: {restart.Summary}");
                }

                caddy = await caddyIntegrationService.GetDashboardAsync(cancellationToken);
            }

            if (!caddy.IsConfigurationValid)
            {
                return new EdgeRuntimePrepareAttempt(
                    false,
                    $"Caddy is installed but its current configuration does not validate: {caddy.ValidationSummary}");
            }

            steps.Add("Caddy is installed, running, and ready for Edge Gateway routing.");
            return new EdgeRuntimePrepareAttempt(true, "Caddy is ready.");
        }
        catch (Exception exception)
        {
            return new EdgeRuntimePrepareAttempt(false, $"Caddy could not be prepared for publishing: {exception.Message}");
        }
    }

    private async Task<string> ResolveSavedCloudflareTokenAsync(CancellationToken cancellationToken)
    {
        var settings = await cloudflareExposureStore.GetSettingsAsync(AiLocalMachine.ManagedHostId, cancellationToken);
        if (string.IsNullOrWhiteSpace(settings?.ApiTokenSecretReference))
        {
            throw new InvalidOperationException("No saved Cloudflare API token is registered for the local LMS host.");
        }

        var token = await secretStore.ResolveSecretAsync(settings.ApiTokenSecretReference, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("The saved Cloudflare API token could not be resolved. Save the token again before provisioning Edge Gateway.");
        }

        return token.Trim();
    }

    private string ResolveCaddyServiceUrl() =>
        string.IsNullOrWhiteSpace(options.CaddyLocalServiceUrl)
            ? "http://localhost:8443"
            : options.CaddyLocalServiceUrl.Trim();

    private static async Task<string> ProbeBackendAsync(string targetUrl, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, targetUrl);
        using var headResponse = await httpClient.SendAsync(
            headRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (headResponse.StatusCode is not HttpStatusCode.MethodNotAllowed and not HttpStatusCode.NotImplemented)
        {
            return $"Backend responded to HEAD with HTTP {(int)headResponse.StatusCode}.";
        }

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, targetUrl);
        using var getResponse = await httpClient.SendAsync(
            getRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        return $"Backend does not support HEAD; GET responded with HTTP {(int)getResponse.StatusCode}.";
    }

    private async Task<GatewayTunnelSetupAttempt> EnsureGatewayTunnelAsync(
        string apiToken,
        CloudflareAccount account,
        CloudflareZone zone,
        string normalizedDomain,
        string gatewaySubdomain,
        string tunnelInstanceId,
        bool replaceExistingDnsRecord,
        bool installConnector,
        CancellationToken cancellationToken)
    {
        var steps = new List<string>();
        var warnings = new List<string>();
        var gatewayDomainName = BuildGatewayDomainName(normalizedDomain, gatewaySubdomain);
        var wildcardHostname = $"*.{gatewayDomainName}";
        var caddyServiceUrl = ResolveCaddyServiceUrl();
        var existingRecords = await cloudflareDnsService.ListRecordsAsync(apiToken, zone.Id, cancellationToken);
        var existingWildcardRecord = existingRecords.FirstOrDefault(record => IsWildcardRecordForDomain(record, gatewayDomainName));
        var tunnels = await cloudflareTunnelService.ListTunnelsAsync(apiToken, account.Id, cancellationToken);
        var connectorStatus = await TryInspectLocalCloudflaredConnectorAsync(cancellationToken);
        var tunnelBaseName = BuildEdgeTunnelBaseName(gatewayDomainName, tunnelInstanceId);
        var tunnelName = BuildUniqueEdgeTunnelName(tunnelBaseName, tunnels);
        var connectorTunnel = ResolveConnectorTunnel(tunnels, connectorStatus);
        var tunnel = ResolveDesiredEdgeGatewayTunnel(tunnels, connectorTunnel, tunnelBaseName);
        if (tunnel is not null)
        {
            steps.Add(IsLocalConnectorTunnel(tunnel, connectorStatus)
                ? connectorStatus?.IsRunning == true
                    ? $"Local cloudflared already uses the LMS Edge Gateway tunnel {tunnel.Name}."
                    : $"Local cloudflared already uses the LMS Edge Gateway tunnel {tunnel.Name}, but the service is not active."
                : $"Reused Cloudflare Tunnel {tunnel.Name} for this LMS relay namespace.");
        }

        if (tunnel is null)
        {
            tunnel = await cloudflareTunnelService.CreateTunnelAsync(apiToken, account.Id, tunnelName, cancellationToken);
            steps.Add($"Created Cloudflare Tunnel {tunnel.Name}.");
        }

        var replaceExistingConnector = ShouldReplaceLocalCloudflaredConnector(connectorStatus, tunnel.Id);
        if (installConnector && replaceExistingConnector)
        {
            steps.Add(BuildCloudflaredReplacementStep(connectorStatus, tunnel.Name));
        }

        var dnsTarget = $"{tunnel.Id}.cfargotunnel.com";
        if (existingWildcardRecord is null)
        {
            await cloudflareDnsService.CreateRecordAsync(
                apiToken,
                zone.Id,
                new CloudflareDnsRecord(
                    string.Empty,
                    zone.Id,
                    wildcardHostname,
                    "CNAME",
                    dnsTarget,
                    true,
                    1,
                    cloudflareOptions.ManagedRecordComment,
                    null),
                cancellationToken);
            steps.Add($"Created scoped wildcard DNS record {wildcardHostname} -> {dnsTarget}.");
        }
        else if (IsSameDnsTarget(existingWildcardRecord, dnsTarget))
        {
            steps.Add($"Scoped wildcard DNS already points at {dnsTarget}.");
        }
        else if (!replaceExistingDnsRecord)
        {
            return new GatewayTunnelSetupAttempt(
                false,
                true,
                tunnel,
                dnsTarget,
                gatewayDomainName,
                wildcardHostname,
                caddyServiceUrl,
                $"Scoped wildcard DNS already exists for {gatewayDomainName} and points at {existingWildcardRecord.Content}. Confirm replacement to point it at the LMS Edge Gateway tunnel.",
                steps,
                [$"Existing scoped wildcard DNS record: {existingWildcardRecord.Type} {existingWildcardRecord.Name} -> {existingWildcardRecord.Content}."]);
        }
        else
        {
            await cloudflareDnsService.DeleteRecordAsync(apiToken, zone.Id, existingWildcardRecord.Id, cancellationToken);
            await cloudflareDnsService.CreateRecordAsync(
                apiToken,
                zone.Id,
                new CloudflareDnsRecord(
                    string.Empty,
                    zone.Id,
                    wildcardHostname,
                    "CNAME",
                    dnsTarget,
                    true,
                    1,
                    cloudflareOptions.ManagedRecordComment,
                    null),
                cancellationToken);
            steps.Add($"Replaced scoped wildcard DNS record with {wildcardHostname} -> {dnsTarget}.");
        }

        var configuration = await cloudflareTunnelService.GetConfigurationAsync(apiToken, account.Id, tunnel.Id, cancellationToken);
        await cloudflareTunnelService.UpdateConfigurationAsync(
            apiToken,
            account.Id,
            tunnel.Id,
            new CloudflareTunnelConfiguration(MergeWildcardTunnelRoute(configuration.Routes, wildcardHostname, caddyServiceUrl)),
            cancellationToken);
        steps.Add($"Configured tunnel ingress {wildcardHostname} -> {caddyServiceUrl}.");

        var connectorSucceeded = true;
        if (installConnector)
        {
            var connectorResult = await EnsureLocalCloudflaredConnectorAsync(
                apiToken,
                account.Id,
                tunnel.Id,
                tunnel.Name,
                connectorStatus,
                replaceExistingConnector,
                cancellationToken);
            connectorSucceeded = connectorResult.Succeeded;
            if (!connectorResult.Succeeded)
            {
                warnings.Add(connectorResult.Summary);
            }

            steps.Add(connectorResult.Summary);
        }

        return new GatewayTunnelSetupAttempt(
            connectorSucceeded,
            false,
            tunnel,
            dnsTarget,
            gatewayDomainName,
            wildcardHostname,
            caddyServiceUrl,
            connectorSucceeded
                ? $"Cloudflare tunnel and scoped wildcard are configured for {gatewayDomainName}."
                : $"Cloudflare tunnel and scoped wildcard are configured for {gatewayDomainName}, but the local cloudflared connector is not running.",
            steps,
            warnings);
    }

    private async Task<string> GetGatewaySubdomainAsync(CancellationToken cancellationToken) =>
        (await GetGatewaySettingsAsync(cancellationToken)).GatewaySubdomain;

    private async Task<EdgeGatewaySettings> GetGatewaySettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        var gatewaySubdomain = NormalizeGatewaySubdomain(settings.GatewaySubdomain);
        var tunnelInstanceId = NormalizeTunnelInstanceId(settings.TunnelInstanceId);
        if (settings.GatewaySubdomain == gatewaySubdomain &&
            settings.TunnelInstanceId == tunnelInstanceId)
        {
            return settings;
        }

        var updated = settings with
        {
            GatewaySubdomain = gatewaySubdomain,
            TunnelInstanceId = tunnelInstanceId,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await settingsStore.SaveAsync(updated, cancellationToken);
        return updated;
    }

    private static string BuildGatewayDomainName(string zoneDomainName, string gatewaySubdomain)
    {
        var normalizedZoneDomain = EdgeGatewayRouteValidator.NormalizeDomainName(zoneDomainName);
        var normalizedNamespace = NormalizeGatewaySubdomain(gatewaySubdomain);
        if (normalizedNamespace.Equals(normalizedZoneDomain, StringComparison.OrdinalIgnoreCase) ||
            normalizedNamespace.EndsWith($".{normalizedZoneDomain}", StringComparison.OrdinalIgnoreCase))
        {
            return EdgeGatewayRouteValidator.NormalizeDomainName(normalizedNamespace);
        }

        if (normalizedNamespace.Contains('.', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Edge Gateway relay namespace {normalizedNamespace} is outside Cloudflare zone {normalizedZoneDomain}. Enter a single label such as relay or a hostname under {normalizedZoneDomain}.");
        }

        return EdgeGatewayRouteValidator.NormalizeDomainName($"{normalizedNamespace}.{normalizedZoneDomain}");
    }

    private static string NormalizeGatewaySubdomain(string? gatewaySubdomain)
    {
        var value = string.IsNullOrWhiteSpace(gatewaySubdomain)
            ? EdgeGatewayDefaultNamespace.BuildForMachineName(Environment.MachineName)
            : gatewaySubdomain.Trim().TrimEnd('.').ToLowerInvariant();
        if (value.StartsWith("*.", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        value = value.Trim('.');
        if (value.Contains('*') ||
            value.Contains('/') ||
            value.Contains('\\') ||
            value.Contains(' '))
        {
            throw new InvalidOperationException("Edge Gateway relay namespace must be a DNS label such as lms-host-relay, or a hostname under the selected Cloudflare zone.");
        }

        _ = value.Contains('.', StringComparison.Ordinal)
            ? EdgeGatewayRouteValidator.NormalizeDomainName(value)
            : EdgeGatewayRouteValidator.NormalizeHostname($"{value}.example.test");
        return value;
    }

    private static string BuildUniqueEdgeTunnelName(string baseName, IReadOnlyList<CloudflareTunnel> existingTunnels)
    {
        var usedNames = existingTunnels
            .Where(static tunnel => !tunnel.IsDeleted)
            .Select(static tunnel => tunnel.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!usedNames.Contains(baseName))
        {
            return baseName;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{baseName}-{suffix}";
            if (!usedNames.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{baseName}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    private string BuildEdgeTunnelBaseName(string domainName, string tunnelInstanceId)
    {
        var slug = string.Concat(
                domainName
                    .Trim()
                    .ToLowerInvariant()
                    .Select(static character => char.IsLetterOrDigit(character) ? character : '-'))
            .Trim('-');

        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "domain";
        }

        var prefix = string.IsNullOrWhiteSpace(cloudflareOptions.ManagedTunnelNamePrefix)
            ? "linux-made-sane"
            : cloudflareOptions.ManagedTunnelNamePrefix.Trim();
        var instanceSlug = BuildTunnelInstanceSlug(tunnelInstanceId);
        return $"{prefix}-edge-{slug[..Math.Min(slug.Length, 32)]}-{instanceSlug}";
    }

    private bool IsRelayTunnelOwnedByThisLms(
        CloudflareTunnel? tunnel,
        string gatewayDomainName,
        string tunnelInstanceId,
        CloudflaredConnectorStatus? connectorStatus) =>
        tunnel is not null &&
        !tunnel.IsDeleted &&
        (tunnel.Name.Equals(BuildEdgeTunnelBaseName(gatewayDomainName, tunnelInstanceId), StringComparison.OrdinalIgnoreCase) ||
         IsLocalConnectorTunnel(tunnel, connectorStatus));

    private static bool IsLocalConnectorTunnel(
        CloudflareTunnel tunnel,
        CloudflaredConnectorStatus? connectorStatus) =>
        connectorStatus is { IsInstalled: true } &&
        !string.IsNullOrWhiteSpace(connectorStatus.TunnelId) &&
        tunnel.Id.Equals(connectorStatus.TunnelId, StringComparison.OrdinalIgnoreCase);

    private static CloudflareTunnel? ResolveDesiredEdgeGatewayTunnel(
        IReadOnlyList<CloudflareTunnel> tunnels,
        CloudflareTunnel? connectorTunnel,
        string tunnelBaseName)
    {
        if (connectorTunnel is not null)
        {
            return connectorTunnel;
        }

        return tunnels.FirstOrDefault(tunnel =>
            !tunnel.IsDeleted &&
            tunnel.Name.Equals(tunnelBaseName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldReplaceLocalCloudflaredConnector(
        CloudflaredConnectorStatus? connectorStatus,
        string desiredTunnelId) =>
        connectorStatus is { IsInstalled: true } &&
        (string.IsNullOrWhiteSpace(connectorStatus.TunnelId) ||
         !connectorStatus.TunnelId.Equals(desiredTunnelId, StringComparison.OrdinalIgnoreCase));

    private static string BuildCloudflaredReplacementStep(
        CloudflaredConnectorStatus? connectorStatus,
        string tunnelName)
    {
        if (connectorStatus is not { IsInstalled: true })
        {
            return $"LMS will install cloudflared for Edge Gateway tunnel {tunnelName}.";
        }

        return string.IsNullOrWhiteSpace(connectorStatus.TunnelId)
            ? $"Local cloudflared is already installed without a readable tunnel id; LMS will replace it with Edge Gateway tunnel {tunnelName}."
            : $"Local cloudflared is attached to tunnel {connectorStatus.TunnelId}; LMS will replace it with Edge Gateway tunnel {tunnelName}.";
    }

    private static string NormalizeTunnelInstanceId(string? tunnelInstanceId)
    {
        var normalized = string.Concat((tunnelInstanceId ?? string.Empty)
            .Where(static character => char.IsLetterOrDigit(character)))
            .ToLowerInvariant();
        return normalized.Length >= 8 ? normalized : Guid.NewGuid().ToString("N");
    }

    private static string BuildTunnelInstanceSlug(string tunnelInstanceId)
    {
        var normalized = NormalizeTunnelInstanceId(tunnelInstanceId);
        return normalized[..Math.Min(normalized.Length, 12)];
    }

    private static CloudflareAccount? ResolveCloudflareAccount(
        IReadOnlyList<CloudflareAccount> accounts,
        CloudflareZone zone)
    {
        if (!string.IsNullOrWhiteSpace(zone.AccountId))
        {
            var byId = accounts.FirstOrDefault(account =>
                account.Id.Equals(zone.AccountId, StringComparison.Ordinal));
            if (byId is not null)
            {
                return byId;
            }

            return new CloudflareAccount(
                zone.AccountId,
                string.IsNullOrWhiteSpace(zone.AccountName) ? zone.Name : zone.AccountName,
                string.Empty,
                null);
        }

        if (!string.IsNullOrWhiteSpace(zone.AccountName))
        {
            var byName = accounts.FirstOrDefault(account =>
                account.Name.Equals(zone.AccountName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName;
            }
        }

        return accounts.Count == 1 ? accounts[0] : null;
    }

    private static bool IsWildcardRecordForDomain(CloudflareDnsRecord record, string domainName)
    {
        var name = (record.Name ?? string.Empty).Trim().TrimEnd('.');
        var wildcard = $"*.{domainName}";
        return name.Equals(wildcard, StringComparison.OrdinalIgnoreCase) ||
               name.Equals("*", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameDnsTarget(CloudflareDnsRecord record, string dnsTarget) =>
        record.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase) &&
        record.Content.Trim().TrimEnd('.').Equals(dnsTarget.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private bool IsResettableEdgeGatewayDnsRecord(
        CloudflareDnsRecord record,
        ISet<string> routeHostnames,
        ISet<string> routeRelayHostnames,
        ISet<string> ownedTunnelDnsTargets)
    {
        if (!IsManagedCloudflareDnsRecord(record))
        {
            return false;
        }

        var name = NormalizeDnsHostname(record.Name);
        var content = NormalizeDnsHostname(record.Content);
        return ownedTunnelDnsTargets.Contains(content) ||
               (routeHostnames.Contains(name) && routeRelayHostnames.Contains(content));
    }

    private static bool IsDnsRecordInRelayNamespace(CloudflareDnsRecord record, string gatewayDomainName) =>
        IsWildcardRecordForDomain(record, gatewayDomainName) ||
        IsHostnameInsideDomain(record.Name, gatewayDomainName);

    private static bool IsDnsRecordPointingIntoRelayNamespace(CloudflareDnsRecord record, string gatewayDomainName) =>
        record.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase) &&
        IsHostnameInsideDomain(record.Content, gatewayDomainName);

    private static bool IsHostnameInsideDomain(string? hostname, string domainName)
    {
        var normalizedHostname = NormalizeDnsHostname(hostname);
        if (string.IsNullOrWhiteSpace(normalizedHostname))
        {
            return false;
        }

        var normalizedDomain = NormalizeDnsHostname(domainName);
        return normalizedHostname.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase) ||
               normalizedHostname.EndsWith($".{normalizedDomain}", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDnsHostname(string? hostname) =>
        (hostname ?? string.Empty).Trim().TrimEnd('.');

    private bool IsManagedCloudflareDnsRecord(CloudflareDnsRecord record) =>
        !string.IsNullOrWhiteSpace(record.Comment) &&
        record.Comment.Contains(cloudflareOptions.ManagedRecordComment, StringComparison.OrdinalIgnoreCase);

    private static string? TryExtractTunnelIdFromDnsTarget(string? dnsTarget)
    {
        if (string.IsNullOrWhiteSpace(dnsTarget) ||
            !dnsTarget.EndsWith(".cfargotunnel.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return dnsTarget[..^".cfargotunnel.com".Length].Trim().TrimEnd('.');
    }

    private static IReadOnlyList<CloudflareTunnelRoute> MergeWildcardTunnelRoute(
        IReadOnlyList<CloudflareTunnelRoute> existingRoutes,
        string wildcardHostname,
        string caddyServiceUrl)
    {
        var routes = existingRoutes
            .Where(route => !string.IsNullOrWhiteSpace(route.Hostname) &&
                            !route.Hostname.Equals(wildcardHostname, StringComparison.OrdinalIgnoreCase))
            .ToList();

        routes.Add(new CloudflareTunnelRoute(wildcardHostname, caddyServiceUrl, BuildEdgeGatewayOriginRequest(caddyServiceUrl)));
        routes.Add(new CloudflareTunnelRoute(string.Empty, "http_status:404"));
        return routes;
    }

    private static IReadOnlyList<CloudflareTunnelRoute> RemoveWildcardTunnelRoute(
        IReadOnlyList<CloudflareTunnelRoute> existingRoutes,
        string wildcardHostname)
    {
        var routes = existingRoutes
            .Where(route => string.IsNullOrWhiteSpace(route.Hostname) ||
                            !route.Hostname.Equals(wildcardHostname, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (routes.All(static route => !string.IsNullOrWhiteSpace(route.Hostname)))
        {
            routes.Add(new CloudflareTunnelRoute(string.Empty, "http_status:404"));
        }

        return routes;
    }

    private static IReadOnlyList<CloudflareTunnelRoute> RemoveTunnelRoutes(
        IReadOnlyList<CloudflareTunnelRoute> existingRoutes,
        ISet<string> hostnames)
    {
        var routes = existingRoutes
            .Where(route => string.IsNullOrWhiteSpace(route.Hostname) ||
                            !hostnames.Contains(route.Hostname.Trim().TrimEnd('.')))
            .ToList();
        if (routes.All(static route => !string.IsNullOrWhiteSpace(route.Hostname)))
        {
            routes.Add(new CloudflareTunnelRoute(string.Empty, "http_status:404"));
        }

        return routes;
    }

    private static IReadOnlyList<CloudflareTunnelRoute> MergeHostnameTunnelRoute(
        IReadOnlyList<CloudflareTunnelRoute> existingRoutes,
        string hostname,
        string caddyServiceUrl)
    {
        var routes = existingRoutes
            .Where(route => !string.IsNullOrWhiteSpace(route.Hostname) &&
                            !route.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase))
            .ToList();

        routes.Add(new CloudflareTunnelRoute(hostname, caddyServiceUrl, BuildEdgeGatewayOriginRequest(caddyServiceUrl)));
        routes.Add(new CloudflareTunnelRoute(string.Empty, "http_status:404"));
        return routes;
    }

    private static string ResolveRelativeHostname(string hostname, string domainName)
    {
        if (hostname.Equals(domainName, StringComparison.OrdinalIgnoreCase))
        {
            return "root";
        }

        var suffix = $".{domainName}";
        if (!hostname.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The route hostname must sit inside the selected Cloudflare zone.");
        }

        return hostname[..^suffix.Length];
    }

    private static CloudflareOriginRequestSettings BuildEdgeGatewayOriginRequest(string caddyServiceUrl)
    {
        if (!Uri.TryCreate(caddyServiceUrl, UriKind.Absolute, out var uri))
        {
            return CloudflareOriginRequestSettings.Default;
        }

        var isLocalHttps = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                           (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                            uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                            uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase));
        return CloudflareOriginRequestSettings.Default with
        {
            NoTlsVerify = isLocalHttps
        };
    }

    private async Task<CloudflaredInstallAttempt> EnsureLocalCloudflaredConnectorAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        string tunnelName,
        CloudflaredConnectorStatus? connectorStatus,
        bool replaceExistingConnector,
        CancellationToken cancellationToken)
    {
        if (connectorStatus is { IsInstalled: true })
        {
            if (!string.IsNullOrWhiteSpace(connectorStatus.TunnelId) &&
                connectorStatus.TunnelId.Equals(tunnelId, StringComparison.OrdinalIgnoreCase))
            {
                if (connectorStatus.IsRunning)
                {
                    return new CloudflaredInstallAttempt(true, $"cloudflared is already running for tunnel {tunnelName}.");
                }

                return await TryStartLocalCloudflaredServiceAsync(tunnelName, cancellationToken);
            }

            if (replaceExistingConnector)
            {
                var replacement = await ReplaceLocalCloudflaredConnectorAsync(
                    apiToken,
                    accountId,
                    tunnelId,
                    connectorStatus.TunnelId,
                    cancellationToken);
                return replacement.Succeeded
                    ? replacement with { Summary = $"Replaced existing cloudflared connector with LMS Edge Gateway tunnel {tunnelName}." }
                    : replacement;
            }

            return new CloudflaredInstallAttempt(
                false,
                string.IsNullOrWhiteSpace(connectorStatus.TunnelId)
                    ? "cloudflared is already installed on this LMS host, so Edge Gateway did not attempt a second service install."
                    : $"cloudflared is already installed for tunnel {connectorStatus.TunnelId}. Edge Gateway must reuse that tunnel or the public hostname will return Cloudflare 1033.");
        }

        return await TryInstallLocalCloudflaredConnectorAsync(apiToken, accountId, tunnelId, cancellationToken);
    }

    private async Task<CloudflaredInstallAttempt> ReplaceLocalCloudflaredConnectorAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        string? previousTunnelId,
        CancellationToken cancellationToken)
    {
        var script = string.Join(
            '\n',
            [
                "sudo systemctl stop cloudflared.service >/dev/null 2>&1 || true",
                "if command -v cloudflared >/dev/null 2>&1; then",
                "  sudo cloudflared service uninstall >/dev/null 2>&1 || true",
                "fi",
                "sudo systemctl daemon-reload >/dev/null 2>&1 || true"
            ]);

        try
        {
            var result = await commandExecutionService.ExecuteAsync(
                AiLocalMachine.CreateManagedHost(),
                WrapShellCommand(script),
                cancellationToken: cancellationToken);
            if (!result.IsSuccess)
            {
                return new CloudflaredInstallAttempt(
                    false,
                    $"cloudflared is installed for tunnel {previousTunnelId ?? "unknown"}, but LMS could not remove the legacy service: {FirstNonEmpty(result.StandardError, result.StandardOutput, $"exit {result.ExitCode}")}");
            }
        }
        catch (Exception exception)
        {
            return new CloudflaredInstallAttempt(
                false,
                $"cloudflared is installed for tunnel {previousTunnelId ?? "unknown"}, but LMS could not remove the legacy service: {exception.Message}");
        }

        return await TryInstallLocalCloudflaredConnectorAsync(apiToken, accountId, tunnelId, cancellationToken);
    }

    private async Task<CloudflaredInstallAttempt> TryStartLocalCloudflaredServiceAsync(
        string tunnelName,
        CancellationToken cancellationToken)
    {
        var script = string.Join(
            '\n',
            [
                "sudo systemctl enable --now cloudflared.service >/dev/null 2>&1 || sudo systemctl start cloudflared.service",
                "systemctl is-active cloudflared.service"
            ]);

        try
        {
            var result = await commandExecutionService.ExecuteAsync(
                AiLocalMachine.CreateManagedHost(),
                WrapShellCommand(script),
                cancellationToken: cancellationToken);
            return result.IsSuccess
                ? new CloudflaredInstallAttempt(true, $"cloudflared service started for tunnel {tunnelName}.")
                : new CloudflaredInstallAttempt(false, $"cloudflared is installed for tunnel {tunnelName}, but LMS could not start it: {FirstNonEmpty(result.StandardError, result.StandardOutput, $"exit {result.ExitCode}")}");
        }
        catch (Exception exception)
        {
            return new CloudflaredInstallAttempt(false, $"cloudflared is installed for tunnel {tunnelName}, but LMS could not start it: {exception.Message}");
        }
    }

    private async Task<CloudflaredInstallAttempt> TryInstallLocalCloudflaredConnectorAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken)
    {
        string tunnelToken;
        try
        {
            tunnelToken = await cloudflareTunnelService.GetTunnelTokenAsync(apiToken, accountId, tunnelId, cancellationToken);
        }
        catch (Exception exception)
        {
            return new CloudflaredInstallAttempt(false, $"Cloudflare tunnel was created, but LMS could not fetch the tunnel connector token: {exception.Message}");
        }

        if (string.IsNullOrWhiteSpace(tunnelToken))
        {
            return new CloudflaredInstallAttempt(false, "Cloudflare tunnel was created, but Cloudflare did not return a connector token.");
        }

        var script = string.Join(
            '\n',
            [
                BuildEnsureCloudflaredInstalledScript(),
                $"sudo cloudflared service install {QuoteShellArgument(tunnelToken.Trim())}",
                "sudo systemctl enable --now cloudflared.service >/dev/null 2>&1 || sudo systemctl start cloudflared.service",
                "systemctl is-active cloudflared.service"
            ]);

        try
        {
            var result = await commandExecutionService.ExecuteAsync(
                AiLocalMachine.CreateManagedHost(),
                WrapShellCommand(script),
                cancellationToken: cancellationToken);
            return result.IsSuccess
                ? new CloudflaredInstallAttempt(true, "cloudflared connector is installed and running for this tunnel.")
                : new CloudflaredInstallAttempt(false, $"cloudflared connector install did not complete: {FirstNonEmpty(result.StandardError, result.StandardOutput, $"exit {result.ExitCode}")}");
        }
        catch (Exception exception)
        {
            return new CloudflaredInstallAttempt(false, $"cloudflared connector install could not be run on this LMS host: {exception.Message}");
        }
    }

    private static string BuildEnsureCloudflaredInstalledScript() =>
        string.Join(
            '\n',
            [
                "download_file() {",
                "  url=\"$1\"",
                "  destination=\"$2\"",
                "  if command -v curl >/dev/null 2>&1; then",
                "    curl -fsSL \"$url\" -o \"$destination\"",
                "  elif command -v wget >/dev/null 2>&1; then",
                "    wget -qO \"$destination\" \"$url\"",
                "  else",
                "    echo 'curl or wget is required to download cloudflared.' >&2",
                "    return 127",
                "  fi",
                "}",
                "install_cloudflared() {",
                "  if command -v cloudflared >/dev/null 2>&1; then",
                "    return 0",
                "  fi",
                "  echo 'cloudflared is not installed; installing it now.'",
                "  arch=$(uname -m)",
                "  case \"$arch\" in",
                "    x86_64|amd64) deb_arch='amd64'; rpm_arch='x86_64' ;;",
                "    aarch64|arm64) deb_arch='arm64'; rpm_arch='aarch64' ;;",
                "    armv7l|armhf) deb_arch='armhf'; rpm_arch='' ;;",
                "    *) echo \"Unsupported cloudflared architecture: $arch\" >&2; return 127 ;;",
                "  esac",
                "  tmp_dir=$(mktemp -d)",
                "  trap 'rm -rf \"$tmp_dir\"' EXIT",
                "  if command -v dpkg >/dev/null 2>&1; then",
                "    package=\"$tmp_dir/cloudflared.deb\"",
                "    download_file \"https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-${deb_arch}.deb\" \"$package\"",
                "    sudo dpkg -i \"$package\"",
                "  elif command -v rpm >/dev/null 2>&1 && [ -n \"$rpm_arch\" ]; then",
                "    package=\"$tmp_dir/cloudflared.rpm\"",
                "    download_file \"https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-${rpm_arch}.rpm\" \"$package\"",
                "    sudo rpm -Uvh \"$package\"",
                "  else",
                "    echo 'This Linux distribution cannot be auto-configured for cloudflared by LMS yet.' >&2",
                "    return 127",
                "  fi",
                "  command -v cloudflared >/dev/null 2>&1",
                "}",
                "install_cloudflared"
            ]);

    private async Task<CloudflaredConnectorStatus?> TryInspectLocalCloudflaredConnectorAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await commandExecutionService.ExecuteAsync(
                AiLocalMachine.CreateManagedHost(),
                BuildCloudflaredInspectionCommand(),
                cancellationToken: cancellationToken);

            if (!result.IsSuccess && string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return null;
            }

            return ParseCloudflaredConnectorStatus(result.StandardOutput);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildCloudflaredInspectionCommand()
    {
        var script = string.Join(
            '\n',
            [
                "fragment=$(systemctl show -p FragmentPath --value cloudflared.service 2>/dev/null || true)",
                "active=$(systemctl is-active cloudflared.service 2>/dev/null || true)",
                "if [ -n \"$fragment\" ] && [ -f \"$fragment\" ]; then",
                "  exec_line=$(sed -n 's/^ExecStart=//p' \"$fragment\" | head -n 1)",
                "  printf 'installed=1\\nactive=%s\\nfragment=%s\\nexec=%s\\n' \"$active\" \"$fragment\" \"$exec_line\"",
                "else",
                "  printf 'installed=0\\nactive=%s\\nfragment=%s\\n' \"$active\" \"$fragment\"",
                "fi"
            ]);

        return WrapShellCommand(script);
    }

    private static CloudflaredConnectorStatus ParseCloudflaredConnectorStatus(string output)
    {
        var values = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => line.Split('=', 2))
            .Where(static parts => parts.Length == 2)
            .ToDictionary(static parts => parts[0], static parts => parts[1], StringComparer.OrdinalIgnoreCase);

        var isInstalled = values.TryGetValue("installed", out var installedValue) &&
                          installedValue == "1";
        var isRunning = values.TryGetValue("active", out var activeValue) &&
                        activeValue.Equals("active", StringComparison.OrdinalIgnoreCase);
        values.TryGetValue("fragment", out var serviceFilePath);
        values.TryGetValue("exec", out var execLine);

        return new CloudflaredConnectorStatus(
            isInstalled,
            isRunning,
            string.IsNullOrWhiteSpace(serviceFilePath) ? null : serviceFilePath,
            TryExtractTunnelIdFromExecStart(execLine));
    }

    private static string? TryExtractTunnelIdFromExecStart(string? execLine)
    {
        if (string.IsNullOrWhiteSpace(execLine))
        {
            return null;
        }

        const string marker = "--token ";
        var tokenIndex = execLine.IndexOf(marker, StringComparison.Ordinal);
        if (tokenIndex < 0)
        {
            return null;
        }

        var rawToken = execLine[(tokenIndex + marker.Length)..].Trim();
        var firstSpaceIndex = rawToken.IndexOf(' ');
        var token = firstSpaceIndex >= 0 ? rawToken[..firstSpaceIndex] : rawToken;
        return TryExtractTunnelIdFromServiceToken(token.Trim('\'', '"'));
    }

    private static string? TryExtractTunnelIdFromServiceToken(string token)
    {
        try
        {
            var normalizedToken = token.Trim()
                .Replace('-', '+')
                .Replace('_', '/');
            var padding = normalizedToken.Length % 4;
            if (padding > 0)
            {
                normalizedToken = normalizedToken.PadRight(normalizedToken.Length + (4 - padding), '=');
            }

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(normalizedToken));
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("t", out var tunnelIdElement)
                ? tunnelIdElement.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static CloudflareTunnel? ResolveConnectorTunnel(
        IReadOnlyList<CloudflareTunnel> tunnels,
        CloudflaredConnectorStatus? connectorStatus)
    {
        if (connectorStatus is not { IsInstalled: true } ||
            string.IsNullOrWhiteSpace(connectorStatus.TunnelId))
        {
            return null;
        }

        return tunnels.FirstOrDefault(tunnel =>
            !tunnel.IsDeleted &&
            tunnel.Id.Equals(connectorStatus.TunnelId, StringComparison.OrdinalIgnoreCase));
    }

    private static string WrapShellCommand(string script) =>
        $"/bin/sh -lc {QuoteShellArgument(script)}";

    private static string QuoteShellArgument(string value) =>
        value.Length == 0
            ? "''"
            : $"'{value.Replace("'", "'\"'\"'")}'";

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private sealed record EdgeRuntimePrepareAttempt(bool Succeeded, string Summary);

    private sealed record CloudflaredInstallAttempt(bool Succeeded, string Summary);

    private sealed record CloudflaredConnectorStatus(
        bool IsInstalled,
        bool IsRunning,
        string? ServiceFilePath,
        string? TunnelId);

    private sealed record GatewayTunnelSetupAttempt(
        bool Success,
        bool RequiresDnsReplacement,
        CloudflareTunnel Tunnel,
        string DnsTarget,
        string GatewayDomainName,
        string WildcardHostname,
        string CaddyServiceUrl,
        string Summary,
        IReadOnlyList<string> Steps,
        IReadOnlyList<string> Warnings);

    private static EdgeGatewayRouteListItem MapListItem(EdgeGatewayRoute route) =>
        new(
            route.Id,
            route.Enabled,
            route.DisplayName,
            route.Hostname,
            route.DomainName,
            route.TargetPathPrefix,
            EdgeGatewayRouteValidator.BuildTargetUrl(route),
            NormalizePublicAuthMode(route.AuthMode),
            route.LastTestStatus,
            route.LastTestMessage,
            route.UpdatedAt);

    private static string NormalizeRequiredText(string? value, string errorMessage)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException(errorMessage);
        }

        return normalized;
    }

    private static string NormalizeHostnameOrSubdomain(string value, string domainName)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("Enter a route hostname or subdomain.");
        }

        if (trimmed.Contains('.', StringComparison.Ordinal))
        {
            return EdgeGatewayRouteValidator.NormalizeHostname(trimmed);
        }

        return EdgeGatewayRouteValidator.NormalizeHostname($"{trimmed}.{domainName}");
    }

    private static string NormalizeCsvOrLines(string? value) =>
        string.Join(", ", EdgeGatewayRouteValidator.SplitList(value));

    private static string? FindFirstValue(ClaimsPrincipal user, string claimType) =>
        user.FindFirst(claimType)?.Value;
}
