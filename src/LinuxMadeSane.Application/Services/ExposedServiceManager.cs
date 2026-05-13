using LinuxMadeSane.Application.Contracts.Cloudflare;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Application.Services.Cloudflare;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Cloudflare;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LinuxMadeSane.Application.Services;

public sealed class ExposedServiceManager(
    IManagedHostStore hostStore,
    ICommandExecutionService commandExecutionService,
    ICloudflareExposureStore exposureStore,
    ICloudflareZoneService zoneService,
    ICloudflareDnsService dnsService,
    ICloudflareTunnelService tunnelService,
    ICloudflareAccessService accessService,
    ISecretStore secretStore,
    CloudflareIntegrationOptions integrationOptions) : IExposedServiceManager
{
    public async Task<CloudflareExposeServiceWorkspaceViewModel> GetWorkspaceAsync(
        Guid hostId,
        CancellationToken cancellationToken = default)
    {
        var host = await GetHostAsync(hostId, cancellationToken);
        var settings = await exposureStore.GetSettingsAsync(hostId, cancellationToken);
        var configs = await exposureStore.ListConfigsAsync(hostId, cancellationToken);
        var connectorStatus = await TryInspectCloudflaredConnectorAsync(host, cancellationToken);
        var (serviceEntries, syncWarnings) = await BuildWorkspaceServiceEntriesAsync(
            host.Id,
            settings,
            configs,
            cancellationToken);

        return new CloudflareExposeServiceWorkspaceViewModel(
            host.Id,
            host.Name,
            host.Description,
            settings,
            serviceEntries,
            syncWarnings,
            connectorStatus);
    }

    public async Task<CloudflareValidationResult> ValidateTokenAsync(
        Guid hostId,
        string? apiToken,
        CancellationToken cancellationToken = default)
    {
        _ = await GetHostAsync(hostId, cancellationToken);
        var settings = await exposureStore.GetSettingsAsync(hostId, cancellationToken);
        var resolvedToken = await ResolveApiTokenAsync(hostId, apiToken, settings, cancellationToken);
        var accounts = await zoneService.ListAccountsAsync(resolvedToken, cancellationToken);
        var zones = await zoneService.ListZonesAsync(resolvedToken, cancellationToken);

        return new CloudflareValidationResult(
            !string.IsNullOrWhiteSpace(settings?.ApiTokenSecretReference),
            accounts,
            zones);
    }

    public async Task<ExposedServiceConnectorSetupResult> EnsureConnectorAsync(
        Guid hostId,
        CloudflareExposeServiceEditor editor,
        CancellationToken cancellationToken = default)
    {
        var host = await GetHostAsync(hostId, cancellationToken);
        var storedSettings = await exposureStore.GetSettingsAsync(hostId, cancellationToken);
        var apiToken = await ResolveApiTokenAsync(hostId, editor.ApiTokenInput, storedSettings, cancellationToken);
        var accounts = await zoneService.ListAccountsAsync(apiToken, cancellationToken);
        var zones = await zoneService.ListZonesAsync(apiToken, cancellationToken);
        var zone = ResolveConnectorZone(zones, editor.ZoneId, storedSettings);
        if (zone is null)
        {
            throw new InvalidOperationException("The Cloudflare token is valid, but it did not return any zones/domains LMS can use.");
        }

        var account = ResolveAccount(accounts, zone, editor.AccountId);
        if (account is null)
        {
            throw new InvalidOperationException($"Cloudflare did not return an account that owns {zone.Name}.");
        }

        await SaveSettingsCoreAsync(hostId, editor, storedSettings, account, zone, cancellationToken);

        var connectorStatus = await TryInspectCloudflaredConnectorAsync(host, cancellationToken);
        var tunnels = await tunnelService.ListTunnelsAsync(apiToken, account.Id, cancellationToken);
        var defaultTunnelName = BuildTunnelName(host.Name, host.Id);
        var tunnel = ResolveConnectorSetupTunnel(tunnels, connectorStatus, editor, defaultTunnelName);
        if (tunnel is null)
        {
            tunnel = await tunnelService.CreateTunnelAsync(
                apiToken,
                account.Id,
                EnsureUniqueTunnelName(defaultTunnelName, tunnels),
                cancellationToken);
        }

        await EnsureTunnelHasFallbackRouteAsync(apiToken, account.Id, tunnel.Id, cancellationToken);

        var tunnelToken = await TryGetTunnelTokenAsync(apiToken, account.Id, tunnel.Id, cancellationToken);
        var connectorInstallCommand = string.IsNullOrWhiteSpace(tunnelToken)
            ? null
            : BuildConnectorInstallCommand(tunnelToken);
        var connectorDeployment = await ResolveConnectorDeploymentAsync(
            host,
            connectorStatus,
            tunnel,
            connectorInstallCommand,
            editor.RunConnectorInstallOnHost,
            cancellationToken);
        var succeeded = connectorDeployment is { Succeeded: true };

        return new ExposedServiceConnectorSetupResult(
            succeeded,
            tunnel,
            succeeded
                ? "Cloudflare connector is ready."
                : "Cloudflare tunnel is ready, but the local connector needs attention.",
            BuildConnectorSetupNextStep(connectorStatus, tunnel, connectorInstallCommand, connectorDeployment),
            connectorInstallCommand,
            connectorDeployment,
            "sudo systemctl status cloudflared --no-pager",
            "sudo journalctl -u cloudflared -n 50 --no-pager");
    }

    public async Task<IReadOnlyList<CloudflareDnsRecord>> ListZoneRecordsAsync(
        Guid hostId,
        string zoneId,
        string? apiToken = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(zoneId))
        {
            return [];
        }

        var settings = await exposureStore.GetSettingsAsync(hostId, cancellationToken);
        var resolvedToken = await ResolveApiTokenAsync(hostId, apiToken, settings, cancellationToken);
        return await dnsService.ListRecordsAsync(resolvedToken, zoneId.Trim(), cancellationToken);
    }

    public async Task<IReadOnlyList<CloudflareTunnel>> ListTunnelsAsync(
        Guid hostId,
        string zoneId,
        string selectedAccountId,
        string? apiToken = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(zoneId))
        {
            return [];
        }

        _ = await GetHostAsync(hostId, cancellationToken);
        var settings = await exposureStore.GetSettingsAsync(hostId, cancellationToken);
        var resolvedToken = await ResolveApiTokenAsync(hostId, apiToken, settings, cancellationToken);
        var zones = await zoneService.ListZonesAsync(resolvedToken, cancellationToken);
        var zone = zones.FirstOrDefault(item => item.Id == zoneId.Trim());
        if (zone is null)
        {
            return [];
        }

        var accounts = await zoneService.ListAccountsAsync(resolvedToken, cancellationToken);
        var account = ResolveAccount(accounts, zone, selectedAccountId);
        if (account is null)
        {
            return [];
        }

        return await tunnelService.ListTunnelsAsync(resolvedToken, account.Id, cancellationToken);
    }

    public async Task<ExposedServiceDryRunPlan> PreviewAsync(
        Guid hostId,
        CloudflareExposeServiceEditor editor,
        string? currentUserEmail,
        CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(hostId, editor, currentUserEmail, cancellationToken);
        return prepared.Plan;
    }

    public async Task<ExposedServiceApplyResult> ApplyAsync(
        Guid hostId,
        CloudflareExposeServiceEditor editor,
        string? currentUserEmail,
        CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(hostId, editor, currentUserEmail, cancellationToken);

        if (prepared.DnsConflict.Kind == CloudflareDnsConflictKind.Conflict &&
            !editor.ConfirmDnsRecordReplacement)
        {
            throw new InvalidOperationException("Review the DNS conflict and confirm deleting/replacing the existing Cloudflare DNS record before applying changes.");
        }

        if (RequiresDangerousExposureConfirmation(prepared.Plan) && !editor.ConfirmDangerousExposure)
        {
            throw new InvalidOperationException("Review and confirm the exposure warnings before applying changes.");
        }

        var tunnel = prepared.Tunnel
            ?? await tunnelService.CreateTunnelAsync(prepared.ApiToken, prepared.Account.Id, prepared.TunnelName, cancellationToken);
        var dnsTarget = $"{tunnel.Id}.cfargotunnel.com";

        CloudflareDnsRecord dnsRecord;
        switch (prepared.DnsConflict.Kind)
        {
            case CloudflareDnsConflictKind.Reuse:
                dnsRecord = prepared.DnsConflict.ExistingRecord!;
                break;
            case CloudflareDnsConflictKind.Update:
                dnsRecord = await dnsService.UpdateRecordAsync(
                    prepared.ApiToken,
                    prepared.Zone.Id,
                    prepared.DnsConflict.ExistingRecord! with
                    {
                        Content = dnsTarget,
                        Proxied = true,
                        Comment = integrationOptions.ManagedRecordComment
                    },
                    cancellationToken);
                break;
            case CloudflareDnsConflictKind.Conflict:
                await dnsService.DeleteRecordAsync(
                    prepared.ApiToken,
                    prepared.Zone.Id,
                    prepared.DnsConflict.ExistingRecord!.Id,
                    cancellationToken);
                dnsRecord = await CreateManagedDnsRecordAsync(prepared, dnsTarget, cancellationToken);
                break;
            default:
                dnsRecord = await CreateManagedDnsRecordAsync(prepared, dnsTarget, cancellationToken);
                break;
        }

        var updatedRoutes = MergeTunnelRoutes(
            prepared.TunnelConfiguration.Routes,
            prepared.Hostname,
            FormatLocalServiceOrigin(prepared.LocalServiceUri),
            prepared.OriginRequestSettings);
        await tunnelService.UpdateConfigurationAsync(
            prepared.ApiToken,
            prepared.Account.Id,
            tunnel.Id,
            new CloudflareTunnelConfiguration(updatedRoutes),
            cancellationToken);

        string? accessApplicationId = prepared.StoredConfig?.AccessApplicationId;
        string? accessPolicyId = prepared.StoredConfig?.AccessPolicyId;

        if (prepared.AccessMode == ExposedServiceAccessMode.NoAccessProtection)
        {
            await DeleteManagedAccessAsync(prepared, cancellationToken);
            accessApplicationId = null;
            accessPolicyId = null;
        }
        else
        {
            var desiredApplication = new CloudflareAccessApplication(
                prepared.AccessApplication?.Id ?? string.Empty,
                prepared.Account.Id,
                prepared.Editor.ServiceName.Trim(),
                prepared.Hostname,
                "self_hosted",
                prepared.AccessApplication?.AudienceTag ?? string.Empty,
                integrationOptions.DefaultAccessSessionDuration);

            var application = prepared.AccessApplication is null
                ? await accessService.CreateApplicationAsync(prepared.ApiToken, prepared.Account.Id, desiredApplication, cancellationToken)
                : await accessService.UpdateApplicationAsync(prepared.ApiToken, prepared.Account.Id, desiredApplication with
                {
                    Id = prepared.AccessApplication.Id,
                    AudienceTag = prepared.AccessApplication.AudienceTag
                }, cancellationToken);

            var desiredPolicy = new CloudflareAccessPolicy(
                prepared.AccessPolicy?.Id ?? string.Empty,
                application.Id,
                BuildPolicyName(prepared.Editor.ServiceName),
                "allow",
                prepared.AllowedEmails,
                prepared.AllowedEmailDomains);

            var policy = prepared.AccessPolicy is null
                ? await accessService.CreatePolicyAsync(prepared.ApiToken, prepared.Account.Id, application.Id, desiredPolicy, cancellationToken)
                : await accessService.UpdatePolicyAsync(prepared.ApiToken, prepared.Account.Id, application.Id, desiredPolicy with
                {
                    Id = prepared.AccessPolicy.Id
                }, cancellationToken);

            accessApplicationId = application.Id;
            accessPolicyId = policy.Id;
        }

        var now = DateTimeOffset.UtcNow;
        await SaveSettingsAsync(hostId, editor, prepared, cancellationToken);

        var config = new ExposedServiceConfig(
            editor.ConfigId ?? prepared.StoredConfig?.Id ?? Guid.NewGuid(),
            hostId,
            prepared.Editor.ServiceName.Trim(),
            prepared.Account.Id,
            prepared.Account.Name,
            prepared.Zone.Id,
            prepared.Zone.Name,
            prepared.Hostname,
            FormatLocalServiceOrigin(prepared.LocalServiceUri),
            tunnel.Id,
            tunnel.Name,
            dnsRecord.Id,
            accessApplicationId,
            accessPolicyId,
            prepared.AccessMode,
            prepared.AllowedEmails,
            prepared.AllowedEmailDomains,
            prepared.StoredConfig?.CreatedAtUtc ?? now,
            now,
            null,
            prepared.OriginRequestSettings);

        await exposureStore.SaveConfigAsync(config, cancellationToken);

        var tunnelToken = await TryGetTunnelTokenAsync(
            prepared.ApiToken,
            prepared.Account.Id,
            tunnel.Id,
            cancellationToken);
        var connectorInstallCommand = string.IsNullOrWhiteSpace(tunnelToken)
            ? null
            : BuildConnectorInstallCommand(tunnelToken);
        var connectorDeployment = await ResolveConnectorDeploymentAsync(
            prepared,
            tunnel,
            connectorInstallCommand,
            cancellationToken);
        var warnings = connectorDeployment is { Succeeded: false }
            ? prepared.Plan.Warnings
                .Concat(
                [
                    new ExposureWarning(
                        "connector-deploy",
                        connectorDeployment.Summary,
                        false)
                ])
                .ToArray()
            : prepared.Plan.Warnings;
        var baseStatus = prepared.AccessMode == ExposedServiceAccessMode.NoAccessProtection
            ? "DNS and Cloudflare Tunnel configuration applied."
            : "DNS, Cloudflare Tunnel, and Access policy applied.";

        return new ExposedServiceApplyResult(
            config,
            $"https://{prepared.Hostname}",
            connectorDeployment switch
            {
                { Succeeded: true } when prepared.ConnectorStatus?.IsInstalled == true
                    => $"{baseStatus} Existing cloudflared service already matches this tunnel.",
                { Succeeded: true } => $"{baseStatus} Connector installed or reattached on the managed host.",
                { Succeeded: false } => $"{baseStatus} Cloudflare-side changes succeeded, but connector deployment on the managed host failed.",
                _ => baseStatus
            },
            BuildNextStepMessage(prepared, connectorInstallCommand, connectorDeployment),
            warnings,
            connectorInstallCommand,
            connectorDeployment,
            "sudo systemctl status cloudflared --no-pager",
            "sudo journalctl -u cloudflared -n 50 --no-pager");
    }

    private async Task<CloudflareDnsRecord> CreateManagedDnsRecordAsync(
        PreparedExposureContext prepared,
        string dnsTarget,
        CancellationToken cancellationToken) =>
        await dnsService.CreateRecordAsync(
            prepared.ApiToken,
            prepared.Zone.Id,
            new CloudflareDnsRecord(
                string.Empty,
                prepared.Zone.Id,
                prepared.Hostname,
                "CNAME",
                dnsTarget,
                true,
                1,
                integrationOptions.ManagedRecordComment,
                null),
            cancellationToken);

    public async Task RemoveAsync(
        Guid hostId,
        Guid configId,
        string? apiToken = null,
        CancellationToken cancellationToken = default)
    {
        var config = await exposureStore.GetConfigAsync(configId, cancellationToken);
        if (config is null || config.ManagedHostId != hostId)
        {
            return;
        }

        await RemoveServiceAsync(hostId, config, removeLmsRecord: true, apiToken, cancellationToken);
    }

    public async Task RemoveServiceAsync(
        Guid hostId,
        ExposedServiceConfig config,
        bool removeLmsRecord,
        string? apiToken = null,
        CancellationToken cancellationToken = default)
    {
        if (config.ManagedHostId != hostId)
        {
            return;
        }

        var settings = await exposureStore.GetSettingsAsync(hostId, cancellationToken);
        var resolvedToken = await ResolveApiTokenAsync(hostId, apiToken, settings, cancellationToken);
        await RemoveCloudflareArtifactsAsync(config, resolvedToken, cancellationToken);

        if (removeLmsRecord)
        {
            await exposureStore.DeleteConfigAsync(config.Id, cancellationToken);
        }
    }

    public async Task DeleteDnsRecordAsync(
        Guid hostId,
        string zoneId,
        string recordId,
        string? apiToken = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(zoneId) || string.IsNullOrWhiteSpace(recordId))
        {
            return;
        }

        var settings = await exposureStore.GetSettingsAsync(hostId, cancellationToken);
        var resolvedToken = await ResolveApiTokenAsync(hostId, apiToken, settings, cancellationToken);
        await TryIgnoreNotFoundAsync(
            () => dnsService.DeleteRecordAsync(
                resolvedToken,
                zoneId.Trim(),
                recordId.Trim(),
                cancellationToken));
    }

    private async Task RemoveCloudflareArtifactsAsync(
        ExposedServiceConfig config,
        string resolvedToken,
        CancellationToken cancellationToken)
    {

        if (!string.IsNullOrWhiteSpace(config.AccessApplicationId) && !string.IsNullOrWhiteSpace(config.AccessPolicyId))
        {
            await TryIgnoreNotFoundAsync(
                () => accessService.DeletePolicyAsync(
                    resolvedToken,
                    config.AccountId,
                    config.AccessApplicationId!,
                    config.AccessPolicyId!,
                    cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(config.AccessApplicationId))
        {
            await TryIgnoreNotFoundAsync(
                () => accessService.DeleteApplicationAsync(
                    resolvedToken,
                    config.AccountId,
                    config.AccessApplicationId!,
                    cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(config.DnsRecordId))
        {
            await TryIgnoreNotFoundAsync(
                () => dnsService.DeleteRecordAsync(
                    resolvedToken,
                    config.ZoneId,
                    config.DnsRecordId,
                    cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(config.TunnelId))
        {
            var existingConfiguration = await tunnelService.GetConfigurationAsync(
                resolvedToken,
                config.AccountId,
                config.TunnelId,
                cancellationToken);

            var updatedRoutes = RemoveTunnelRoute(existingConfiguration.Routes, config.Hostname);
            await tunnelService.UpdateConfigurationAsync(
                resolvedToken,
                config.AccountId,
                    config.TunnelId,
                    new CloudflareTunnelConfiguration(updatedRoutes),
                    cancellationToken);
        }
    }

    public async Task ForgetSavedTokenAsync(Guid hostId, CancellationToken cancellationToken = default)
    {
        var settings = await exposureStore.GetSettingsAsync(hostId, cancellationToken);
        if (settings is null || string.IsNullOrWhiteSpace(settings.ApiTokenSecretReference))
        {
            return;
        }

        await secretStore.DeleteSecretAsync(settings.ApiTokenSecretReference, cancellationToken);
        await exposureStore.SaveSettingsAsync(settings with
        {
            ApiTokenSecretReference = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private async Task<PreparedExposureContext> PrepareAsync(
        Guid hostId,
        CloudflareExposeServiceEditor editor,
        string? currentUserEmail,
        CancellationToken cancellationToken)
    {
        var host = await GetHostAsync(hostId, cancellationToken);
        ValidateEditor(editor);

        var storedSettings = await exposureStore.GetSettingsAsync(hostId, cancellationToken);
        var apiToken = await ResolveApiTokenAsync(hostId, editor.ApiTokenInput, storedSettings, cancellationToken);
        var zones = await zoneService.ListZonesAsync(apiToken, cancellationToken);
        var zone = zones.FirstOrDefault(item => item.Id == editor.ZoneId.Trim());
        if (zone is null)
        {
            throw new InvalidOperationException("Select a Cloudflare zone for this exposure.");
        }

        var accounts = await zoneService.ListAccountsAsync(apiToken, cancellationToken);
        var account = ResolveAccount(accounts, zone, editor.AccountId);
        if (account is null)
        {
            throw new InvalidOperationException("The selected Cloudflare account is no longer available.");
        }

        if (!CloudflareHostnameValidator.TryNormalizeRelativeHostname(editor.Subdomain, out var relativeHostname, out var hostnameError))
        {
            throw new InvalidOperationException(hostnameError);
        }

        var hostname = CloudflareHostnameValidator.BuildAbsoluteHostname(relativeHostname, zone.Name);
        var localServiceUri = ParseLocalServiceUri(editor.LocalServiceUrl);
        var localServiceOrigin = FormatLocalServiceOrigin(localServiceUri);
        var originRequestSettings = BuildOriginRequestSettings(editor, localServiceUri);
        var warnings = DangerousExposureInspector.Inspect(editor.ServiceName, localServiceUri);
        var storedConfig = await exposureStore.GetConfigByHostnameAsync(hostId, hostname, cancellationToken);

        var allowedEmails = ResolveAllowedEmails(editor.AccessMode, editor.EmailAllowList, currentUserEmail);
        var allowedEmailDomains = ResolveAllowedDomains(editor.AccessMode, editor.EmailDomainAllowList);
        var connectorStatus = await TryInspectCloudflaredConnectorAsync(host, cancellationToken);

        var tunnels = await tunnelService.ListTunnelsAsync(apiToken, account.Id, cancellationToken);
        var defaultTunnelName = BuildTunnelName(host.Name, host.Id);
        var (tunnel, tunnelName) = ResolveTunnelSelection(tunnels, storedConfig, editor, defaultTunnelName);
        ValidateConnectorReuse(connectorStatus, tunnel, editor.CreateNewTunnel);

        var tunnelConfiguration = tunnel is null
            ? new CloudflareTunnelConfiguration([])
            : await tunnelService.GetConfigurationAsync(apiToken, account.Id, tunnel.Id, cancellationToken);
        var existingRoute = tunnelConfiguration.Routes.FirstOrDefault(item =>
            item.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase));

        var dnsRecords = await dnsService.ListRecordsAsync(apiToken, zone.Id, cancellationToken);
        var expectedDnsTarget = tunnel is null ? null : $"{tunnel.Id}.cfargotunnel.com";
        var dnsConflict = CloudflareDnsConflictDetector.Detect(
            hostname,
            expectedDnsTarget,
            dnsRecords,
            storedConfig?.DnsRecordId,
            integrationOptions.ManagedRecordComment);

        if (dnsConflict.Kind == CloudflareDnsConflictKind.Conflict)
        {
            warnings = warnings
                .Concat(
                [
                    new ExposureWarning(
                        "dns-conflict",
                        $"{dnsConflict.Reason} Confirm replacement to delete that Cloudflare DNS record and create a proxied CNAME for this LMS tunnel.",
                        true)
                ])
                .ToArray();
        }

        CloudflareAccessApplication? accessApplication = null;
        CloudflareAccessPolicy? accessPolicy = null;

        if (editor.AccessMode != ExposedServiceAccessMode.NoAccessProtection)
        {
            var applications = await accessService.ListApplicationsAsync(apiToken, account.Id, cancellationToken);
            accessApplication = applications.FirstOrDefault(item =>
                (!string.IsNullOrWhiteSpace(storedConfig?.AccessApplicationId) && item.Id == storedConfig.AccessApplicationId) ||
                item.Domain.Equals(hostname, StringComparison.OrdinalIgnoreCase));

            if (accessApplication is not null)
            {
                var policies = await accessService.ListPoliciesAsync(apiToken, account.Id, accessApplication.Id, cancellationToken);
                accessPolicy = policies.FirstOrDefault(item =>
                    (!string.IsNullOrWhiteSpace(storedConfig?.AccessPolicyId) && item.Id == storedConfig.AccessPolicyId) ||
                    item.Name.Equals(BuildPolicyName(editor.ServiceName), StringComparison.OrdinalIgnoreCase));
            }
        }

        var plan = ExposedServiceDryRunPlanner.Build(
            new ExposedServicePlanningContext(
                hostname,
                $"https://{hostname}",
                tunnelName,
                localServiceOrigin,
                expectedDnsTarget ?? "{new tunnel id}.cfargotunnel.com",
                editor.AccessMode != ExposedServiceAccessMode.NoAccessProtection,
                dnsConflict.Kind,
                dnsConflict.Reason,
                tunnel is not null,
                existingRoute is not null,
                accessApplication is not null,
                accessPolicy is not null,
                connectorStatus?.IsInstalled == true,
                connectorStatus?.IsInstalled == true &&
                    tunnel is not null &&
                    !string.IsNullOrWhiteSpace(connectorStatus.TunnelId) &&
                    connectorStatus.TunnelId.Equals(tunnel.Id, StringComparison.OrdinalIgnoreCase),
                editor.RunConnectorInstallOnHost,
                warnings,
                originRequestSettings.NoTlsVerify));

        return new PreparedExposureContext(
            host,
            editor,
            apiToken,
            account,
            zone,
            hostname,
            localServiceUri,
            tunnelName,
            tunnel,
            tunnelConfiguration,
            dnsConflict,
            accessApplication,
            accessPolicy,
            storedSettings,
            storedConfig,
            connectorStatus,
            editor.AccessMode,
            allowedEmails,
            allowedEmailDomains,
            originRequestSettings,
            plan);
    }

    private async Task DeleteManagedAccessAsync(PreparedExposureContext prepared, CancellationToken cancellationToken)
    {
        if (prepared.AccessPolicy is not null && prepared.AccessApplication is not null)
        {
            await TryIgnoreNotFoundAsync(
                () => accessService.DeletePolicyAsync(
                    prepared.ApiToken,
                    prepared.Account.Id,
                    prepared.AccessApplication.Id,
                    prepared.AccessPolicy.Id,
                    cancellationToken));
        }

        if (prepared.AccessApplication is not null)
        {
            await TryIgnoreNotFoundAsync(
                () => accessService.DeleteApplicationAsync(
                    prepared.ApiToken,
                    prepared.Account.Id,
                    prepared.AccessApplication.Id,
                    cancellationToken));
        }
    }

    private async Task<CloudflareSettings> SaveSettingsAsync(
        Guid hostId,
        CloudflareExposeServiceEditor editor,
        PreparedExposureContext prepared,
        CancellationToken cancellationToken) =>
        await SaveSettingsCoreAsync(
            hostId,
            editor,
            prepared.StoredSettings,
            prepared.Account,
            prepared.Zone,
            cancellationToken);

    private async Task<CloudflareSettings> SaveSettingsCoreAsync(
        Guid hostId,
        CloudflareExposeServiceEditor editor,
        CloudflareSettings? existingSettings,
        CloudflareAccount account,
        CloudflareZone zone,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var secretReference = existingSettings?.ApiTokenSecretReference;
        var newSecretReference = string.Empty;

        if (editor.ClearSavedApiToken && !string.IsNullOrWhiteSpace(secretReference))
        {
            await secretStore.DeleteSecretAsync(secretReference, cancellationToken);
            secretReference = null;
        }

        if (editor.SaveApiToken && !string.IsNullOrWhiteSpace(editor.ApiTokenInput))
        {
            newSecretReference = await secretStore.StoreSecretAsync(
                editor.ApiTokenInput.Trim(),
                $"cloudflare:{hostId:N}",
                cancellationToken);

            secretReference = newSecretReference;
        }

        var settings = new CloudflareSettings(
            hostId,
            account.Id,
            account.Name,
            zone.Id,
            zone.Name,
            secretReference,
            existingSettings?.CreatedAtUtc ?? now,
            now);

        await exposureStore.SaveSettingsAsync(settings, cancellationToken);

        if (!string.IsNullOrWhiteSpace(newSecretReference) &&
            !string.IsNullOrWhiteSpace(existingSettings?.ApiTokenSecretReference) &&
            !string.Equals(existingSettings.ApiTokenSecretReference, newSecretReference, StringComparison.Ordinal))
        {
            await secretStore.DeleteSecretAsync(existingSettings.ApiTokenSecretReference, cancellationToken);
        }

        return settings;
    }

    private static bool RequiresDangerousExposureConfirmation(ExposedServiceDryRunPlan plan) =>
        plan.Warnings.Any(warning =>
            warning.RequiresConfirmation &&
            !warning.Code.Equals("dns-conflict", StringComparison.OrdinalIgnoreCase));

    private async Task<string> ResolveApiTokenAsync(
        Guid hostId,
        string? suppliedApiToken,
        CloudflareSettings? settings,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(suppliedApiToken))
        {
            return suppliedApiToken.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settings?.ApiTokenSecretReference))
        {
            var resolved = await secretStore.ResolveSecretAsync(settings.ApiTokenSecretReference, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved.Trim();
            }
        }

        throw new InvalidOperationException("Paste a Cloudflare API token or save one securely for this host before continuing.");
    }

    private async Task<LinuxMadeSane.Core.Models.ManagedHost> GetHostAsync(Guid hostId, CancellationToken cancellationToken)
    {
        var host = await hostStore.GetAsync(hostId, cancellationToken);
        if (host is null)
        {
            throw new InvalidOperationException("That managed host no longer exists.");
        }

        return host;
    }

    private async Task<(IReadOnlyList<CloudflareExposedServiceListItemViewModel> Entries, IReadOnlyList<string> Warnings)> BuildWorkspaceServiceEntriesAsync(
        Guid hostId,
        CloudflareSettings? settings,
        IReadOnlyList<ExposedServiceConfig> storedConfigs,
        CancellationToken cancellationToken)
    {
        if (settings is null || string.IsNullOrWhiteSpace(settings.AccountId) || string.IsNullOrWhiteSpace(settings.ZoneId))
        {
            return (BuildStoredOnlyEntries(storedConfigs), Array.Empty<string>());
        }

        try
        {
            var apiToken = await ResolveApiTokenAsync(hostId, null, settings, cancellationToken);
            var discoveredConfigs = await DiscoverCloudflareServicesAsync(hostId, apiToken, settings, cancellationToken);
            return BuildMergedEntries(storedConfigs, discoveredConfigs);
        }
        catch (InvalidOperationException exception) when (string.IsNullOrWhiteSpace(settings.ApiTokenSecretReference))
        {
            return (
                BuildStoredOnlyEntries(storedConfigs),
                storedConfigs.Count == 0
                    ? Array.Empty<string>()
                    : [$"Linux Made Sane could not verify Cloudflare state because no saved API token is available: {exception.Message}"]);
        }
        catch (Exception exception)
        {
            return (
                BuildStoredOnlyEntries(storedConfigs),
                storedConfigs.Count == 0
                    ? [$"Linux Made Sane could not inspect Cloudflare for existing exposed services: {exception.Message}"]
                    : [$"Linux Made Sane could not verify Cloudflare state, so this list currently shows only LMS-stored entries: {exception.Message}"]);
        }
    }

    private async Task<IReadOnlyList<ExposedServiceConfig>> DiscoverCloudflareServicesAsync(
        Guid hostId,
        string apiToken,
        CloudflareSettings settings,
        CancellationToken cancellationToken)
    {
        var dnsRecords = await dnsService.ListRecordsAsync(apiToken, settings.ZoneId, cancellationToken);
        var candidateRecords = dnsRecords
            .Where(record =>
                record.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(record.Name) &&
                record.Content.EndsWith(".cfargotunnel.com", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (candidateRecords.Length == 0)
        {
            return [];
        }

        var tunnels = await tunnelService.ListTunnelsAsync(apiToken, settings.AccountId, cancellationToken);
        var routesByTunnelId = new Dictionary<string, IReadOnlyList<CloudflareTunnelRoute>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tunnelId in candidateRecords
                     .Select(record => TryExtractTunnelIdFromDnsTarget(record.Content))
                     .Where(static tunnelId => !string.IsNullOrWhiteSpace(tunnelId))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var configuration = await tunnelService.GetConfigurationAsync(apiToken, settings.AccountId, tunnelId!, cancellationToken);
            routesByTunnelId[tunnelId!] = configuration.Routes;
        }

        var applications = await accessService.ListApplicationsAsync(apiToken, settings.AccountId, cancellationToken);
        var policiesByApplicationId = new Dictionary<string, IReadOnlyList<CloudflareAccessPolicy>>(StringComparer.OrdinalIgnoreCase);
        var discoveredConfigs = new List<ExposedServiceConfig>();

        foreach (var record in candidateRecords)
        {
            var tunnelId = TryExtractTunnelIdFromDnsTarget(record.Content);
            if (string.IsNullOrWhiteSpace(tunnelId))
            {
                continue;
            }

            var tunnel = tunnels.FirstOrDefault(item => item.Id.Equals(tunnelId, StringComparison.OrdinalIgnoreCase));
            routesByTunnelId.TryGetValue(tunnelId, out var routes);
            var route = routes?.FirstOrDefault(item => item.Hostname.Equals(record.Name, StringComparison.OrdinalIgnoreCase));
            var application = applications.FirstOrDefault(item => item.Domain.Equals(record.Name, StringComparison.OrdinalIgnoreCase));

            if (!ShouldTrackDiscoveredService(record, tunnel, route, application))
            {
                continue;
            }

            CloudflareAccessPolicy? policy = null;
            if (application is not null)
            {
                if (!policiesByApplicationId.TryGetValue(application.Id, out var policies))
                {
                    policies = await accessService.ListPoliciesAsync(apiToken, settings.AccountId, application.Id, cancellationToken);
                    policiesByApplicationId[application.Id] = policies;
                }

                policy = policies
                    .Where(item => item.Decision.Equals("allow", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.Name.Equals(BuildPolicyName(application.Name), StringComparison.OrdinalIgnoreCase))
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }

            discoveredConfigs.Add(
                new ExposedServiceConfig(
                    CreateSyntheticConfigId(hostId, record.Name),
                    hostId,
                    !string.IsNullOrWhiteSpace(application?.Name) ? application.Name : record.Name,
                    settings.AccountId,
                    settings.AccountName,
                    settings.ZoneId,
                    settings.ZoneName,
                    record.Name,
                    route?.Service ?? string.Empty,
                    tunnelId,
                    tunnel?.Name ?? tunnelId,
                    record.Id,
                    application?.Id,
                    policy?.Id,
                    InferAccessMode(application, policy),
                    policy?.IncludeEmails ?? [],
                    policy?.IncludeEmailDomains ?? [],
                    record.ModifiedAtUtc ?? DateTimeOffset.UtcNow,
                    record.ModifiedAtUtc ?? DateTimeOffset.UtcNow,
                    null,
                    route?.OriginRequest));
        }

        return discoveredConfigs
            .OrderBy(item => item.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static (IReadOnlyList<CloudflareExposedServiceListItemViewModel> Entries, IReadOnlyList<string> Warnings) BuildMergedEntries(
        IReadOnlyList<ExposedServiceConfig> storedConfigs,
        IReadOnlyList<ExposedServiceConfig> discoveredConfigs)
    {
        var storedByHostname = storedConfigs.ToDictionary(item => item.Hostname, StringComparer.OrdinalIgnoreCase);
        var discoveredByHostname = discoveredConfigs.ToDictionary(item => item.Hostname, StringComparer.OrdinalIgnoreCase);
        var hostnames = storedByHostname.Keys
            .Concat(discoveredByHostname.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var entries = new List<CloudflareExposedServiceListItemViewModel>(hostnames.Length);
        foreach (var hostname in hostnames)
        {
            var hasStored = storedByHostname.TryGetValue(hostname, out var stored);
            var hasDiscovered = discoveredByHostname.TryGetValue(hostname, out var discovered);

            if (hasStored && hasDiscovered)
            {
                entries.Add(
                    new CloudflareExposedServiceListItemViewModel(
                        MergeWorkspaceConfig(stored!, discovered!),
                        true,
                        true,
                        false,
                        "Live in Cloudflare."));
                continue;
            }

            if (hasDiscovered)
            {
                entries.Add(
                    new CloudflareExposedServiceListItemViewModel(
                        discovered!,
                        false,
                        true,
                        false,
                        "Live in Cloudflare."));
                continue;
            }

            entries.Add(
                new CloudflareExposedServiceListItemViewModel(
                    stored!,
                    true,
                    false,
                    false,
                    "Saved in Linux Made Sane. Cloudflare live state was not matched; use the external link to test the route."));
        }

        var warnings = entries
            .Where(item => item.IsOutOfSync && !item.ExistsInCloudflare)
            .Select(item => $"{item.Config.Hostname}: {item.SyncSummary}")
            .ToArray();

        return (entries, warnings);
    }

    private static IReadOnlyList<CloudflareExposedServiceListItemViewModel> BuildStoredOnlyEntries(IReadOnlyList<ExposedServiceConfig> storedConfigs) =>
        storedConfigs
            .OrderBy(item => item.Hostname, StringComparer.OrdinalIgnoreCase)
            .Select(item => new CloudflareExposedServiceListItemViewModel(
                item,
                true,
                false,
                false,
                "Tracked in Linux Made Sane. Cloudflare sync has not been checked yet."))
            .ToArray();

    private static Uri ParseLocalServiceUri(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Enter an absolute local service URL such as http://localhost:8080.");
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only http:// and https:// local service targets are supported in this release.");
        }

        if (!string.IsNullOrWhiteSpace(uri.Query) || !string.IsNullOrWhiteSpace(uri.Fragment))
        {
            throw new InvalidOperationException("Do not include a query string or fragment in the local service URL. Use only the origin, for example http://localhost:8080.");
        }

        if (!string.IsNullOrWhiteSpace(uri.AbsolutePath) && uri.AbsolutePath != "/")
        {
            throw new InvalidOperationException("Do not include a path in the local service URL. Cloudflare Tunnel ingress expects only the origin, for example http://localhost:5080.");
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
    }

    private static string FormatLocalServiceOrigin(Uri uri) =>
        uri.GetLeftPart(UriPartial.Authority);

    private static IReadOnlyList<string> ResolveAllowedEmails(
        ExposedServiceAccessMode accessMode,
        string rawEmailList,
        string? currentUserEmail)
    {
        return accessMode switch
        {
            ExposedServiceAccessMode.OnlyMe when !string.IsNullOrWhiteSpace(currentUserEmail) => [currentUserEmail.Trim()],
            ExposedServiceAccessMode.OnlyMe => throw new InvalidOperationException("The current login does not include an email address, so Only me cannot be used."),
            ExposedServiceAccessMode.EmailAllowList => ParseList(rawEmailList, "email allow list"),
            _ => []
        };
    }

    private static IReadOnlyList<string> ResolveAllowedDomains(
        ExposedServiceAccessMode accessMode,
        string rawDomainList)
    {
        return accessMode == ExposedServiceAccessMode.EmailDomainAllowList
            ? ParseList(rawDomainList, "email domain allow list")
                .Select(item => item.Trim().TrimStart('@'))
                .ToArray()
            : [];
    }

    private string BuildTunnelName(string hostName, Guid hostId)
    {
        var slug = string.Concat(
            hostName
                .Trim()
                .ToLowerInvariant()
                .Select(character => char.IsLetterOrDigit(character) ? character : '-'))
            .Trim('-');

        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "host";
        }

        var hostSuffix = hostId.ToString("N")[..8];
        return $"{integrationOptions.ManagedTunnelNamePrefix}-{slug[..Math.Min(slug.Length, 24)]}-{hostSuffix}";
    }

    private static string BuildPolicyName(string serviceName) =>
        $"{serviceName.Trim()} access";

    private static (CloudflareTunnel? Tunnel, string TunnelName) ResolveTunnelSelection(
        IReadOnlyList<CloudflareTunnel> tunnels,
        ExposedServiceConfig? storedConfig,
        CloudflareExposeServiceEditor editor,
        string defaultTunnelName)
    {
        if (editor.CreateNewTunnel)
        {
            return (null, EnsureUniqueTunnelName(defaultTunnelName, tunnels));
        }

        var trimmedTunnelId = editor.TunnelId.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedTunnelId))
        {
            var selectedTunnel = tunnels.FirstOrDefault(item => item.Id.Equals(trimmedTunnelId, StringComparison.Ordinal));
            if (selectedTunnel is null)
            {
                throw new InvalidOperationException("The selected Cloudflare tunnel is no longer available.");
            }

            return (selectedTunnel, selectedTunnel.Name);
        }

        var resolvedTunnel = tunnels.FirstOrDefault(item => storedConfig is not null && item.Id == storedConfig.TunnelId)
            ?? tunnels.FirstOrDefault(item => item.Name.Equals(defaultTunnelName, StringComparison.OrdinalIgnoreCase));

        return resolvedTunnel is null
            ? (null, defaultTunnelName)
            : (resolvedTunnel, resolvedTunnel.Name);
    }

    private static CloudflareZone? ResolveConnectorZone(
        IReadOnlyList<CloudflareZone> zones,
        string selectedZoneId,
        CloudflareSettings? storedSettings)
    {
        var trimmedSelectedZoneId = selectedZoneId.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedSelectedZoneId))
        {
            var selectedZone = zones.FirstOrDefault(item =>
                item.Id.Equals(trimmedSelectedZoneId, StringComparison.Ordinal));
            if (selectedZone is not null)
            {
                return selectedZone;
            }
        }

        if (!string.IsNullOrWhiteSpace(storedSettings?.ZoneId))
        {
            var storedZone = zones.FirstOrDefault(item =>
                item.Id.Equals(storedSettings.ZoneId, StringComparison.Ordinal));
            if (storedZone is not null)
            {
                return storedZone;
            }
        }

        return zones
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static CloudflareTunnel? ResolveConnectorSetupTunnel(
        IReadOnlyList<CloudflareTunnel> tunnels,
        CloudflaredConnectorStatus? connectorStatus,
        CloudflareExposeServiceEditor editor,
        string defaultTunnelName)
    {
        if (connectorStatus is { IsInstalled: true } &&
            !string.IsNullOrWhiteSpace(connectorStatus.TunnelId))
        {
            var connectorTunnel = tunnels.FirstOrDefault(item =>
                !item.IsDeleted &&
                item.Id.Equals(connectorStatus.TunnelId, StringComparison.OrdinalIgnoreCase));
            if (connectorTunnel is not null)
            {
                return connectorTunnel;
            }
        }

        if (!editor.CreateNewTunnel && !string.IsNullOrWhiteSpace(editor.TunnelId))
        {
            var selectedTunnel = tunnels.FirstOrDefault(item =>
                !item.IsDeleted &&
                item.Id.Equals(editor.TunnelId.Trim(), StringComparison.Ordinal));
            if (selectedTunnel is not null)
            {
                return selectedTunnel;
            }
        }

        return tunnels.FirstOrDefault(item =>
                !item.IsDeleted &&
                item.Name.Equals(defaultTunnelName, StringComparison.OrdinalIgnoreCase))
            ?? tunnels.FirstOrDefault(item => !item.IsDeleted && item.IsManagedByLinuxMadeSane);
    }

    private async Task EnsureTunnelHasFallbackRouteAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken)
    {
        var configuration = await tunnelService.GetConfigurationAsync(apiToken, accountId, tunnelId, cancellationToken);
        if (configuration.Routes.Any(route => string.IsNullOrWhiteSpace(route.Hostname)))
        {
            return;
        }

        var routes = configuration.Routes
            .Where(route => !string.IsNullOrWhiteSpace(route.Hostname))
            .ToList();
        routes.Add(new CloudflareTunnelRoute(string.Empty, "http_status:404"));

        await tunnelService.UpdateConfigurationAsync(
            apiToken,
            accountId,
            tunnelId,
            new CloudflareTunnelConfiguration(routes),
            cancellationToken);
    }

    private static string EnsureUniqueTunnelName(string baseTunnelName, IReadOnlyList<CloudflareTunnel> tunnels)
    {
        var usedNames = tunnels
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!usedNames.Contains(baseTunnelName))
        {
            return baseTunnelName;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{baseTunnelName}-{suffix}";
            if (!usedNames.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{baseTunnelName}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    private static string BuildConnectorInstallCommand(string tunnelToken) =>
        $"sudo cloudflared service install {QuoteShellArgument(tunnelToken.Trim())}";

    private static string BuildNextStepMessage(
        PreparedExposureContext prepared,
        string? connectorInstallCommand,
        ExposedServiceConnectorDeploymentResult? connectorDeployment)
    {
        if (prepared.ConnectorStatus is { IsInstalled: true, TunnelId: not null } connectorStatus &&
            prepared.Tunnel is not null &&
            connectorStatus.TunnelId.Equals(prepared.Tunnel.Id, StringComparison.OrdinalIgnoreCase))
        {
            return connectorStatus.IsRunning
                ? "This managed host already has cloudflared installed for the selected tunnel, so no reinstall was attempted. Cloudflare-side route changes should flow through the existing connector."
                : "This managed host already has cloudflared installed for the selected tunnel, but the service is not currently active. Start or inspect cloudflared with the commands below.";
        }

        if (connectorDeployment is { Succeeded: true })
        {
            return "Linux Made Sane ran the cloudflared install command on the managed host. If the public URL is not live yet, check the service status and recent logs below.";
        }

        if (connectorDeployment is { Succeeded: false })
        {
            return "Cloudflare-side changes are saved, but Linux Made Sane could not finish the cloudflared install on the managed host. Review the deployment output below or run the install command manually on that host.";
        }

        if (string.IsNullOrWhiteSpace(connectorInstallCommand))
        {
            return "Run the Cloudflare-provided cloudflared installation command on the target machine that serves the local service URL. Linux Made Sane could not fetch it automatically, so copy it from the Cloudflare Tunnel dashboard for this tunnel.";
        }

        return prepared.Editor.RunConnectorInstallOnHost
            ? "Linux Made Sane did not run the connector install because Cloudflare did not return a tunnel token. Run the install command manually on the target host."
            : "Run the connector install command on the managed host when you are ready to attach or reattach cloudflared to this tunnel.";
    }

    private static string BuildConnectorSetupNextStep(
        CloudflaredConnectorStatus? connectorStatus,
        CloudflareTunnel tunnel,
        string? connectorInstallCommand,
        ExposedServiceConnectorDeploymentResult? connectorDeployment)
    {
        if (connectorStatus is { IsInstalled: true, IsRunning: true })
        {
            return "This host is already running cloudflared. You can add applications against this connector now.";
        }

        if (connectorStatus is { IsInstalled: true })
        {
            return "cloudflared is installed but not running. Start or inspect the service, then refresh this page before publishing applications.";
        }

        if (connectorDeployment is { Succeeded: true })
        {
            return $"LMS created or reused tunnel {tunnel.Name}, attached cloudflared, and the host is ready for applications.";
        }

        if (connectorDeployment is { Succeeded: false })
        {
            return "LMS created or reused the Cloudflare tunnel, but could not attach cloudflared on this host. Check that cloudflared is installed and sudo is available, then run setup again.";
        }

        return string.IsNullOrWhiteSpace(connectorInstallCommand)
            ? "LMS created or reused the Cloudflare tunnel, but Cloudflare did not return a connector token. Check the token has Cloudflare Tunnel edit permission."
            : "LMS created or reused the Cloudflare tunnel. Run the connector install command on this host, then refresh.";
    }

    private async Task<string?> TryGetTunnelTokenAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken)
    {
        try
        {
            var tunnelToken = await tunnelService.GetTunnelTokenAsync(apiToken, accountId, tunnelId, cancellationToken);
            return string.IsNullOrWhiteSpace(tunnelToken)
                ? null
                : tunnelToken.Trim();
        }
        catch (CloudflareApiException)
        {
            return null;
        }
    }

    private async Task<ExposedServiceConnectorDeploymentResult?> TryDeployConnectorAsync(
        ManagedHost host,
        string? connectorInstallCommand,
        bool runConnectorInstallOnHost,
        CancellationToken cancellationToken)
    {
        if (!runConnectorInstallOnHost || string.IsNullOrWhiteSpace(connectorInstallCommand))
        {
            return null;
        }

        try
        {
            var executionResult = await commandExecutionService.ExecuteAsync(
                host,
                BuildAutomatedConnectorInstallCommand(connectorInstallCommand),
                cancellationToken: cancellationToken);

            return new ExposedServiceConnectorDeploymentResult(
                executionResult.IsSuccess,
                connectorInstallCommand,
                executionResult.ExitCode,
                executionResult.IsSuccess
                    ? "cloudflared was installed or reattached on the managed host."
                    : "The cloudflared install command failed on the managed host.",
                executionResult.StandardOutput,
                executionResult.StandardError);
        }
        catch (Exception exception)
        {
            return new ExposedServiceConnectorDeploymentResult(
                false,
                connectorInstallCommand,
                null,
                "Linux Made Sane could not execute the cloudflared install command on the managed host.",
                string.Empty,
                exception.Message);
        }
    }

    private async Task<ExposedServiceConnectorDeploymentResult?> ResolveConnectorDeploymentAsync(
        PreparedExposureContext prepared,
        CloudflareTunnel tunnel,
        string? connectorInstallCommand,
        CancellationToken cancellationToken) =>
        await ResolveConnectorDeploymentAsync(
            prepared.Host,
            prepared.ConnectorStatus,
            tunnel,
            connectorInstallCommand,
            prepared.Editor.RunConnectorInstallOnHost,
            cancellationToken);

    private async Task<ExposedServiceConnectorDeploymentResult?> ResolveConnectorDeploymentAsync(
        ManagedHost host,
        CloudflaredConnectorStatus? connectorStatus,
        CloudflareTunnel tunnel,
        string? connectorInstallCommand,
        bool runConnectorInstallOnHost,
        CancellationToken cancellationToken)
    {
        if (connectorStatus is { IsInstalled: true })
        {
            return new ExposedServiceConnectorDeploymentResult(
                true,
                string.Empty,
                connectorStatus.IsRunning ? 0 : null,
                !string.IsNullOrWhiteSpace(connectorStatus.TunnelId) &&
                connectorStatus.TunnelId.Equals(tunnel.Id, StringComparison.OrdinalIgnoreCase)
                    ? connectorStatus.IsRunning
                        ? "cloudflared is already installed and running for the selected tunnel."
                        : "cloudflared is already installed for the selected tunnel, but the service is not currently active."
                    : "cloudflared is already installed on this host, so Linux Made Sane did not attempt a second service install.",
                string.Empty,
                string.Empty);
        }

        return await TryDeployConnectorAsync(
            host,
            connectorInstallCommand,
            runConnectorInstallOnHost,
            cancellationToken);
    }

    private async Task<CloudflaredConnectorStatus?> TryInspectCloudflaredConnectorAsync(
        ManagedHost host,
        CancellationToken cancellationToken)
    {
        try
        {
            var inspectionResult = await commandExecutionService.ExecuteAsync(
                host,
                BuildCloudflaredInspectionCommand(),
                cancellationToken: cancellationToken);

            if (!inspectionResult.IsSuccess && string.IsNullOrWhiteSpace(inspectionResult.StandardOutput))
            {
                return null;
            }

            return ParseCloudflaredConnectorStatus(inspectionResult.StandardOutput);
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
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

        var isInstalled = values.TryGetValue("installed", out var installedValue) &&
            installedValue == "1";
        var isRunning = values.TryGetValue("active", out var activeValue) &&
            activeValue.Equals("active", StringComparison.OrdinalIgnoreCase);
        values.TryGetValue("fragment", out var serviceFilePath);
        values.TryGetValue("exec", out var execLine);
        var tunnelId = TryExtractTunnelIdFromExecStart(execLine);

        return new CloudflaredConnectorStatus(
            isInstalled,
            isRunning,
            string.IsNullOrWhiteSpace(serviceFilePath) ? null : serviceFilePath,
            tunnelId);
    }

    private static string? TryExtractTunnelIdFromExecStart(string? execLine)
    {
        if (string.IsNullOrWhiteSpace(execLine))
        {
            return null;
        }

        var marker = "--token ";
        var tokenIndex = execLine.IndexOf(marker, StringComparison.Ordinal);
        if (tokenIndex < 0)
        {
            return null;
        }

        var rawToken = execLine[(tokenIndex + marker.Length)..].Trim();
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return null;
        }

        var firstSpaceIndex = rawToken.IndexOf(' ');
        var token = firstSpaceIndex >= 0 ? rawToken[..firstSpaceIndex] : rawToken;
        return TryExtractTunnelIdFromServiceToken(token);
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

    private static void ValidateConnectorReuse(
        CloudflaredConnectorStatus? connectorStatus,
        CloudflareTunnel? selectedTunnel,
        bool createNewTunnel)
    {
        if (connectorStatus is not { IsInstalled: true })
        {
            return;
        }

        if (createNewTunnel || selectedTunnel is null)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(connectorStatus.TunnelId)
                    ? "This managed host already has cloudflared installed. Reuse the existing tunnel on this machine instead of creating a new one."
                    : $"This managed host already has cloudflared installed for tunnel {connectorStatus.TunnelId}. Reuse that tunnel instead of creating a new one.");
        }

        if (!string.IsNullOrWhiteSpace(connectorStatus.TunnelId) &&
            !selectedTunnel.Id.Equals(connectorStatus.TunnelId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"This managed host already has cloudflared installed for tunnel {connectorStatus.TunnelId}. Select that tunnel instead of {selectedTunnel.Name} so LMS reuses the existing local connector.");
        }
    }

    private static string BuildAutomatedConnectorInstallCommand(string connectorInstallCommand)
    {
        var script = string.Join(
            '\n',
            [
                BuildEnsureCloudflaredInstalledScript(),
                connectorInstallCommand,
                "sudo systemctl enable --now cloudflared.service >/dev/null 2>&1 || sudo systemctl start cloudflared.service",
                "systemctl is-active cloudflared.service"
            ]);

        return WrapShellCommand(script);
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

    private static string WrapShellCommand(string script) =>
        $"/bin/sh -lc {QuoteShellArgument(script)}";

    private static string QuoteShellArgument(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        return $"'{value.Replace("'", "'\"'\"'")}'";
    }

    private static CloudflareAccount? ResolveAccount(
        IReadOnlyList<CloudflareAccount> accounts,
        CloudflareZone zone,
        string selectedAccountId)
    {
        var trimmedSelectedAccountId = selectedAccountId.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedSelectedAccountId))
        {
            var selectedAccount = accounts.FirstOrDefault(item =>
                item.Id.Equals(trimmedSelectedAccountId, StringComparison.Ordinal));

            if (selectedAccount is not null)
            {
                return selectedAccount;
            }
        }

        var trimmedZoneAccountId = zone.AccountId.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedZoneAccountId))
        {
            var zoneAccount = accounts.FirstOrDefault(item =>
                item.Id.Equals(trimmedZoneAccountId, StringComparison.Ordinal));

            if (zoneAccount is not null)
            {
                return zoneAccount;
            }
        }

        var trimmedZoneAccountName = zone.AccountName.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedZoneAccountName))
        {
            var namedAccount = accounts.FirstOrDefault(item =>
                item.Name.Equals(trimmedZoneAccountName, StringComparison.OrdinalIgnoreCase));

            if (namedAccount is not null)
            {
                return namedAccount;
            }
        }

        if (!string.IsNullOrWhiteSpace(trimmedZoneAccountId))
        {
            return new CloudflareAccount(
                trimmedZoneAccountId,
                string.IsNullOrWhiteSpace(trimmedZoneAccountName) ? zone.Name : trimmedZoneAccountName,
                string.Empty,
                null);
        }

        return accounts.Count == 1 ? accounts[0] : null;
    }

    private static void ValidateEditor(CloudflareExposeServiceEditor editor)
    {
        if (string.IsNullOrWhiteSpace(editor.ZoneId))
        {
            throw new InvalidOperationException("Choose a Cloudflare zone.");
        }

        if (string.IsNullOrWhiteSpace(editor.ServiceName))
        {
            throw new InvalidOperationException("Enter a friendly service name.");
        }
    }

    private static IReadOnlyList<string> ParseList(string rawValue, string label)
    {
        var items = rawValue
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (items.Length == 0)
        {
            throw new InvalidOperationException($"Add at least one value to the {label}.");
        }

        return items;
    }

    private static IReadOnlyList<CloudflareTunnelRoute> MergeTunnelRoutes(
        IReadOnlyList<CloudflareTunnelRoute> existingRoutes,
        string hostname,
        string service,
        CloudflareOriginRequestSettings originRequestSettings)
    {
        var routes = existingRoutes
            .Where(item => !string.IsNullOrWhiteSpace(item.Hostname) && !item.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase))
            .ToList();

        routes.Add(new CloudflareTunnelRoute(hostname, service, originRequestSettings));
        routes.Add(new CloudflareTunnelRoute(string.Empty, "http_status:404"));
        return routes;
    }

    private static CloudflareOriginRequestSettings BuildOriginRequestSettings(CloudflareExposeServiceEditor editor, Uri localServiceUri)
    {
        var isHttps = localServiceUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

        return new CloudflareOriginRequestSettings(
            isHttps ? editor.OriginServerName.Trim() : string.Empty,
            isHttps ? editor.CertificateAuthorityPool.Trim() : string.Empty,
            editor.NoTlsVerify && isHttps,
            Math.Max(1, editor.TlsTimeoutSeconds),
            editor.Http2Origin && isHttps,
            editor.MatchSniToHost && isHttps,
            editor.HttpHostHeader.Trim(),
            editor.DisableChunkedEncoding,
            Math.Max(1, editor.ConnectTimeoutSeconds),
            editor.NoHappyEyeballs,
            NormalizeProxyType(editor.ProxyType),
            Math.Max(1, editor.KeepAliveTimeoutSeconds),
            Math.Max(0, editor.KeepAliveConnections),
            Math.Max(1, editor.TcpKeepAliveSeconds));
    }

    private static string NormalizeProxyType(string? proxyType)
    {
        var value = proxyType?.Trim() ?? string.Empty;
        return value.Equals("socks", StringComparison.OrdinalIgnoreCase) ? "socks" : string.Empty;
    }

    private static IReadOnlyList<CloudflareTunnelRoute> RemoveTunnelRoute(
        IReadOnlyList<CloudflareTunnelRoute> existingRoutes,
        string hostname)
    {
        var routes = existingRoutes
            .Where(item => !item.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.Hostname))
            .ToList();

        routes.Add(new CloudflareTunnelRoute(string.Empty, "http_status:404"));
        return routes;
    }

    private static string? TryExtractTunnelIdFromDnsTarget(string? dnsTarget)
    {
        if (string.IsNullOrWhiteSpace(dnsTarget))
        {
            return null;
        }

        const string suffix = ".cfargotunnel.com";
        var normalized = dnsTarget.Trim();
        return normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? normalized[..^suffix.Length]
            : null;
    }

    private bool ShouldTrackDiscoveredService(
        CloudflareDnsRecord record,
        CloudflareTunnel? tunnel,
        CloudflareTunnelRoute? route,
        CloudflareAccessApplication? application) =>
        record.Comment.Equals(integrationOptions.ManagedRecordComment, StringComparison.OrdinalIgnoreCase) ||
        tunnel?.IsManagedByLinuxMadeSane == true ||
        route is not null ||
        application is not null;

    private static ExposedServiceAccessMode InferAccessMode(
        CloudflareAccessApplication? application,
        CloudflareAccessPolicy? policy)
    {
        if (application is null || policy is null)
        {
            return ExposedServiceAccessMode.NoAccessProtection;
        }

        return policy.IncludeEmailDomains.Count > 0
            ? ExposedServiceAccessMode.EmailDomainAllowList
            : policy.IncludeEmails.Count > 0
                ? ExposedServiceAccessMode.EmailAllowList
                : ExposedServiceAccessMode.NoAccessProtection;
    }

    private static ExposedServiceConfig MergeWorkspaceConfig(
        ExposedServiceConfig stored,
        ExposedServiceConfig discovered) =>
        stored with
        {
            ServiceName = string.IsNullOrWhiteSpace(discovered.ServiceName) ? stored.ServiceName : discovered.ServiceName,
            AccountId = string.IsNullOrWhiteSpace(discovered.AccountId) ? stored.AccountId : discovered.AccountId,
            AccountName = string.IsNullOrWhiteSpace(discovered.AccountName) ? stored.AccountName : discovered.AccountName,
            ZoneId = string.IsNullOrWhiteSpace(discovered.ZoneId) ? stored.ZoneId : discovered.ZoneId,
            ZoneName = string.IsNullOrWhiteSpace(discovered.ZoneName) ? stored.ZoneName : discovered.ZoneName,
            Hostname = string.IsNullOrWhiteSpace(discovered.Hostname) ? stored.Hostname : discovered.Hostname,
            LocalServiceUrl = string.IsNullOrWhiteSpace(discovered.LocalServiceUrl) ? stored.LocalServiceUrl : discovered.LocalServiceUrl,
            TunnelId = string.IsNullOrWhiteSpace(discovered.TunnelId) ? stored.TunnelId : discovered.TunnelId,
            TunnelName = string.IsNullOrWhiteSpace(discovered.TunnelName) ? stored.TunnelName : discovered.TunnelName,
            DnsRecordId = string.IsNullOrWhiteSpace(discovered.DnsRecordId) ? stored.DnsRecordId : discovered.DnsRecordId,
            AccessApplicationId = string.IsNullOrWhiteSpace(discovered.AccessApplicationId) ? stored.AccessApplicationId : discovered.AccessApplicationId,
            AccessPolicyId = string.IsNullOrWhiteSpace(discovered.AccessPolicyId) ? stored.AccessPolicyId : discovered.AccessPolicyId,
            AccessMode = discovered.AccessMode,
            AllowedEmails = discovered.AllowedEmails.Count > 0 ? discovered.AllowedEmails : stored.AllowedEmails,
            AllowedEmailDomains = discovered.AllowedEmailDomains.Count > 0 ? discovered.AllowedEmailDomains : stored.AllowedEmailDomains,
            OriginRequestSettings = discovered.OriginRequestSettings ?? stored.OriginRequestSettings,
            UpdatedAtUtc = Max(stored.UpdatedAtUtc, discovered.UpdatedAtUtc)
        };

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private static Guid CreateSyntheticConfigId(Guid hostId, string hostname)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"{hostId:N}:{hostname.Trim().ToLowerInvariant()}"));
        return new Guid(hash);
    }

    private static async Task TryIgnoreNotFoundAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (CloudflareApiException exception) when (exception.StatusCode == 404)
        {
        }
    }

    private sealed record PreparedExposureContext(
        LinuxMadeSane.Core.Models.ManagedHost Host,
        CloudflareExposeServiceEditor Editor,
        string ApiToken,
        CloudflareAccount Account,
        CloudflareZone Zone,
        string Hostname,
        Uri LocalServiceUri,
        string TunnelName,
        CloudflareTunnel? Tunnel,
        CloudflareTunnelConfiguration TunnelConfiguration,
        CloudflareDnsConflictResult DnsConflict,
        CloudflareAccessApplication? AccessApplication,
        CloudflareAccessPolicy? AccessPolicy,
        CloudflareSettings? StoredSettings,
        ExposedServiceConfig? StoredConfig,
        CloudflaredConnectorStatus? ConnectorStatus,
        ExposedServiceAccessMode AccessMode,
        IReadOnlyList<string> AllowedEmails,
        IReadOnlyList<string> AllowedEmailDomains,
        CloudflareOriginRequestSettings OriginRequestSettings,
        ExposedServiceDryRunPlan Plan);
}
