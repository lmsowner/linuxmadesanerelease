// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Application.Contracts.EdgeGateway;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Abstractions.Portal;
using LinuxMadeSane.Core.Models.Cloudflare;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Services.Cloudflare;
using LinuxMadeSane.Infrastructure.Services;
using LinuxMadeSane.Infrastructure.Stores;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LinuxMadeSane.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRootPath)
    {
        var dataProtectionDirectory = new DirectoryInfo(BuildRootedPath(
            configuration["DataProtection:KeyDirectory"],
            Path.Combine(contentRootPath, "data", "protection-keys"),
            contentRootPath));
        dataProtectionDirectory.Create();

        var connectionString = BuildSqliteConnectionString(
            configuration.GetConnectionString("LinuxMadeSane") ?? "Data Source=data/linuxmadesane.db",
            contentRootPath);

        services.AddDataProtection()
            .SetApplicationName("LinuxMadeSane")
            .PersistKeysToFileSystem(dataProtectionDirectory);
        services.AddOptions<CloudflareIntegrationOptions>()
            .Bind(configuration.GetSection("Cloudflare"))
            .ValidateDataAnnotations();
        services.AddSingleton(serviceProvider => serviceProvider.GetRequiredService<IOptions<CloudflareIntegrationOptions>>().Value);
        services.AddSingleton(configuration.GetSection("EdgeGateway").Get<EdgeGatewayOptions>() ?? new EdgeGatewayOptions());
        services.AddHttpClient<ICloudflareClient, CloudflareClient>();
        services.AddDbContext<LinuxMadeSaneDbContext>(options => options
            .UseSqlite(connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.CommandExecuted)));
        services.AddSingleton<AiChatRunQueue>();
        services.AddSingleton<IAiChatRunQueue>(serviceProvider => serviceProvider.GetRequiredService<AiChatRunQueue>());
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<AiChatRunQueue>());
        services.AddSingleton<MediaLibraryScanQueue>();
        services.AddSingleton<IMediaLibraryScanQueue>(serviceProvider => serviceProvider.GetRequiredService<MediaLibraryScanQueue>());
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<MediaLibraryScanQueue>());
        services.AddScoped<SqliteDatabaseInitializer>();
        services.AddScoped<ManagedHostSshCredentialResolver>();
        services.AddSingleton(new RdpOptimizerStorageSettings(Path.Combine(contentRootPath, "data", "rdp-optimizer")));
        services.AddSingleton(new ShareMountStorageSettings(Path.Combine(contentRootPath, "data", "share-mounts")));
        services.AddSingleton(new SftpBackupStorageSettings(Path.Combine(contentRootPath, "data", "sftp-backups")));
        services.AddHostedService<TemporaryShareMountCleanupService>();
        services.AddScoped<IAiConversationStore, SqliteAiConversationStore>();
        services.AddScoped<IAiProviderSettingsStore, SqliteAiProviderSettingsStore>();
        services.AddScoped<IUserDisplayPreferenceStore, SqliteUserDisplayPreferenceStore>();
        services.AddScoped<IFileBrowserShortcutStore, SqliteFileBrowserShortcutStore>();
        services.AddScoped<ILocalAiEngineStore, SqliteLocalAiEngineStore>();
        services.AddScoped<IAiProviderRegistry, SqliteAiProviderRegistry>();
        services.AddScoped<IAiProviderConnectionTester, AiProviderConnectionTester>();
        services.AddScoped<IAiProviderModelDiscoveryService, AiProviderModelDiscoveryService>();
        services.AddScoped<IAiToolRegistry, LinuxMadeSaneAiToolRegistry>();
        services.AddScoped<IAiSafeChangeService, AiSafeChangeService>();
        services.AddScoped<IAiToolBridge, LinuxMadeSaneAiToolBridge>();
        services.AddSingleton<ILmsConnectClientFeature, DisabledLmsConnectClientFeature>();
        services.AddSingleton<IAiApprovalPolicyService, AiApprovalPolicyService>();
        services.AddScoped<IAiProviderCapabilityService, AiProviderCapabilityService>();
        services.AddScoped<IAiAuditService, SqliteAiAuditService>();
        services.AddScoped<ISecretStore, SqliteProtectedSecretStore>();
        services.AddScoped<IPortalConnectionStore, DisabledPortalConnectionStore>();
        services.AddScoped<ICloudflareExposureStore, SqliteCloudflareExposureStore>();
        services.AddScoped<IEdgeGatewaySettingsStore, SqliteEdgeGatewaySettingsStore>();
        services.AddScoped<IMessagingEmailSettingsStore, SqliteMessagingEmailSettingsStore>();
        services.AddScoped<IEmailDeliveryService, ConfiguredEmailDeliveryService>();
        services.AddHttpClient(nameof(ConfiguredEmailDeliveryService));
        services.AddScoped<ISecurityUserStore, SqliteSecurityUserStore>();
        services.AddScoped<ISecurityPasskeyStore, SqliteSecurityPasskeyStore>();
        services.AddScoped<ILocalInstanceIdentityStore, SqliteLocalInstanceIdentityStore>();
        services.AddScoped<IRemoteAccessSystemService, LocalRemoteAccessSystemService>();
        services.AddScoped<ILocalUserAccessSystemService, LocalUserAccessSystemService>();
        services.AddScoped<ITrustedNetworkStore, SqliteTrustedNetworkStore>();
        services.AddScoped<ITrustedNetworkAccessService, TrustedNetworkAccessService>();
        services.AddScoped<IManagedHostStore, SqliteManagedHostStore>();
        services.AddScoped<IManagedHostHealthProbe, ManagedHostHealthProbe>();
        services.AddScoped<ISavedCommandStore, SqliteSavedCommandStore>();
        services.AddScoped<ILinuxShareModuleDataService, SqliteLinuxShareModuleDataService>();
        services.AddScoped<ILinuxServiceModuleDataService, SqliteLinuxServiceModuleDataService>();
        services.AddScoped<ILinuxSchedulingModuleDataService, SqliteLinuxSchedulingModuleDataService>();
        services.AddScoped<ICaddyIntegrationDataService, SqliteCaddyIntegrationDataService>();
        services.AddScoped<IEdgeGatewayStore, SqliteEdgeGatewayStore>();
        services.AddScoped<IEdgeGatewayCaddyManager, LocalEdgeGatewayCaddyManager>();
        services.AddScoped<IMediaLibraryIntegrationDataService, SqliteMediaLibraryIntegrationDataService>();
        services.AddScoped<ISftpServerStore, SqliteSftpServerStore>();
        services.AddScoped<ISftpAuditService, SqliteSftpAuditService>();
        services.AddScoped<ISftpAuthPolicyService, LocalSftpAuthPolicyService>();
        services.AddScoped<ISftpChrootService, DefaultSftpChrootService>();
        services.AddScoped<ISftpBackupService, FileSystemSftpBackupService>();
        services.AddScoped<ISshdConfigService, SshdConfigService>();
        services.AddScoped<ISftpServerInspectionService, LocalSftpServerInspectionService>();
        services.AddScoped<ISftpServerConfigurationService, LocalSftpServerConfigurationService>();
        services.AddScoped<ISftpUserManagementService, LocalSftpUserManagementService>();
        services.AddScoped<ILinuxCommandRunner, LinuxCommandRunner>();
        services.AddSingleton<ManagedHostSshConnectionFactory>();
        services.AddScoped<ILocalAiHardwareInspectionService, LocalAiHardwareInspectionService>();
        services.AddScoped<ILocalModelManagementService, LocalModelManagementService>();
        services.AddScoped<IOllamaRuntimeService, OllamaRuntimeService>();
        services.AddScoped<IRemoteLmsAiEngineGateway, DisabledRemoteLmsAiEngineGateway>();
        services.AddScoped<ISshHostDiscoveryService, SshHostDiscoveryService>();
        services.AddScoped<IDesktopInspectionService, DesktopInspectionService>();
        services.AddScoped<IPackageManagementService, AptPackageManagementService>();
        services.AddScoped<IServiceManagementService, SystemdServiceManagementService>();
        services.AddScoped<ISessionConfigurationService, XrdpSessionConfigurationService>();
        services.AddScoped<IRestoreSnapshotService, JsonRestoreSnapshotService>();
        services.AddScoped<IHostSecretsService, StoredHostSecretsService>();
        services.AddScoped<ISshKeyPairGenerator, SshKeyPairGenerator>();
        services.AddScoped<ICloudflareZoneService, CloudflareZoneService>();
        services.AddScoped<ICloudflareDnsService, CloudflareDnsService>();
        services.AddScoped<ICloudflareTunnelService, CloudflareTunnelService>();
        services.AddScoped<ICloudflareAccessService, CloudflareAccessService>();
        services.AddScoped<ISshConnectionService, SshConnectionService>();
        services.AddScoped<ICommandExecutionService, ManagedHostCommandExecutionService>();
        services.AddScoped<ILocalHttpServiceDiscoveryService, LocalHttpServiceDiscoveryService>();
        services.AddScoped<IManagedHostFileAccessService, ManagedHostFileAccessService>();
        services.AddSingleton<ITerminalSessionService, SshTerminalSessionService>();
        services.AddScoped<ILocalFileBrowsingService, LocalFileBrowsingService>();
        services.AddScoped<ISftpFileBrowsingService, SshSftpFileBrowsingService>();
        return services;
    }

    private static string BuildSqliteConnectionString(string connectionString, string contentRootPath)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);

        if (!string.IsNullOrWhiteSpace(builder.DataSource) && !Path.IsPathRooted(builder.DataSource))
        {
            builder.DataSource = Path.GetFullPath(builder.DataSource, contentRootPath);
        }

        return builder.ToString();
    }

    private static string BuildRootedPath(string? configuredPath, string fallbackPath, string contentRootPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? fallbackPath
            : configuredPath.Trim();

        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(path, contentRootPath);
    }
}
