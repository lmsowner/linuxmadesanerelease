using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Persistence;

public sealed class LinuxMadeSaneDbContext(DbContextOptions<LinuxMadeSaneDbContext> options) : DbContext(options)
{
    public DbSet<ManagedHostEntity> ManagedHosts => Set<ManagedHostEntity>();
    public DbSet<SavedCommandEntity> SavedCommands => Set<SavedCommandEntity>();
    public DbSet<SambaShareEntity> SambaShares => Set<SambaShareEntity>();
    public DbSet<LinuxShareUserEntity> LinuxShareUsers => Set<LinuxShareUserEntity>();
    public DbSet<LocalUserAccessPolicyEntity> LocalUserAccessPolicies => Set<LocalUserAccessPolicyEntity>();
    public DbSet<LinuxShareGroupEntity> LinuxShareGroups => Set<LinuxShareGroupEntity>();
    public DbSet<LinuxServiceEntity> LinuxServices => Set<LinuxServiceEntity>();
    public DbSet<ScheduledTaskEntity> ScheduledTasks => Set<ScheduledTaskEntity>();
    public DbSet<CaddyProxyRouteEntity> CaddyProxyRoutes => Set<CaddyProxyRouteEntity>();
    public DbSet<EdgeGatewayRouteEntity> EdgeGatewayRoutes => Set<EdgeGatewayRouteEntity>();
    public DbSet<EdgeGatewayAuditEntryEntity> EdgeGatewayAuditEntries => Set<EdgeGatewayAuditEntryEntity>();
    public DbSet<EdgeGatewaySettingsEntity> EdgeGatewaySettings => Set<EdgeGatewaySettingsEntity>();
    public DbSet<MessagingEmailSettingsEntity> MessagingEmailSettings => Set<MessagingEmailSettingsEntity>();
    public DbSet<SftpHostSettingsEntity> SftpHostSettings => Set<SftpHostSettingsEntity>();
    public DbSet<SftpManagedUserEntity> SftpManagedUsers => Set<SftpManagedUserEntity>();
    public DbSet<SftpPublicKeyEntity> SftpPublicKeys => Set<SftpPublicKeyEntity>();
    public DbSet<SftpAuditEntryEntity> SftpAuditEntries => Set<SftpAuditEntryEntity>();
    public DbSet<SftpBackupSnapshotEntity> SftpBackupSnapshots => Set<SftpBackupSnapshotEntity>();
    public DbSet<RemoteShareMountEntity> RemoteShareMounts => Set<RemoteShareMountEntity>();
    public DbSet<AiChatThreadEntity> AiChatThreads => Set<AiChatThreadEntity>();
    public DbSet<AiChatMessageEntity> AiChatMessages => Set<AiChatMessageEntity>();
    public DbSet<AiAttachedServerEntity> AiAttachedServers => Set<AiAttachedServerEntity>();
    public DbSet<AiExecutionPlanEntity> AiExecutionPlans => Set<AiExecutionPlanEntity>();
    public DbSet<AiProposedActionEntity> AiProposedActions => Set<AiProposedActionEntity>();
    public DbSet<AiApprovalRequestEntity> AiApprovalRequests => Set<AiApprovalRequestEntity>();
    public DbSet<AiApprovalDecisionEntity> AiApprovalDecisions => Set<AiApprovalDecisionEntity>();
    public DbSet<AiToolInvocationEntity> AiToolInvocations => Set<AiToolInvocationEntity>();
    public DbSet<AiToolResultEntity> AiToolResults => Set<AiToolResultEntity>();
    public DbSet<AiChatRunEntity> AiChatRuns => Set<AiChatRunEntity>();
    public DbSet<AiAuditEntryEntity> AiAuditEntries => Set<AiAuditEntryEntity>();
    public DbSet<AiChatCheckpointEntity> AiChatCheckpoints => Set<AiChatCheckpointEntity>();
    public DbSet<AiProviderSettingsEntity> AiProviderSettings => Set<AiProviderSettingsEntity>();
    public DbSet<UserDisplayPreferenceEntity> UserDisplayPreferences => Set<UserDisplayPreferenceEntity>();
    public DbSet<FileBrowserShortcutEntity> FileBrowserShortcuts => Set<FileBrowserShortcutEntity>();
    public DbSet<ProtectedSecretEntity> ProtectedSecrets => Set<ProtectedSecretEntity>();
    public DbSet<SecurityUserEntity> SecurityUsers => Set<SecurityUserEntity>();
    public DbSet<SecurityPasskeyCredentialEntity> SecurityPasskeyCredentials => Set<SecurityPasskeyCredentialEntity>();
    public DbSet<LocalInstanceIdentityEntity> LocalInstanceIdentities => Set<LocalInstanceIdentityEntity>();
    public DbSet<TrustedNetworkEntryEntity> TrustedNetworkEntries => Set<TrustedNetworkEntryEntity>();
    public DbSet<CloudflareSettingsEntity> CloudflareSettings => Set<CloudflareSettingsEntity>();
    public DbSet<ExposedServiceConfigEntity> ExposedServiceConfigs => Set<ExposedServiceConfigEntity>();
    public DbSet<PortalConnectionSettingsEntity> PortalConnectionSettings => Set<PortalConnectionSettingsEntity>();
    public DbSet<LocalAiEngineSettingsEntity> LocalAiEngineSettings => Set<LocalAiEngineSettingsEntity>();
    public DbSet<LocalAiInstalledModelEntity> LocalAiInstalledModels => Set<LocalAiInstalledModelEntity>();
    public DbSet<LocalAiHardwareSnapshotEntity> LocalAiHardwareSnapshots => Set<LocalAiHardwareSnapshotEntity>();
    public DbSet<LocalAiBenchmarkResultEntity> LocalAiBenchmarkResults => Set<LocalAiBenchmarkResultEntity>();
    public DbSet<LocalAiUsageEntryEntity> LocalAiUsageEntries => Set<LocalAiUsageEntryEntity>();
    public DbSet<LocalAiAuditEntryEntity> LocalAiAuditEntries => Set<LocalAiAuditEntryEntity>();
    public DbSet<MediaLibrarySettingsEntity> MediaLibrarySettings => Set<MediaLibrarySettingsEntity>();
    public DbSet<MediaLibraryRootEntity> MediaLibraryRoots => Set<MediaLibraryRootEntity>();
    public DbSet<MediaItemEntity> MediaItems => Set<MediaItemEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ManagedHostEntity>(entity =>
        {
            entity.ToTable("managed_hosts");
            entity.HasKey(host => host.Id);
            entity.Property(host => host.Name).HasMaxLength(128);
            entity.Property(host => host.Hostname).HasMaxLength(255);
            entity.Property(host => host.Environment).HasMaxLength(64);
            entity.Property(host => host.DefaultWorkingDirectory).HasMaxLength(255);
            entity.Property(host => host.Username).HasMaxLength(128);
            entity.Property(host => host.PasswordSecretReference).HasMaxLength(255);
            entity.Property(host => host.PrivateKeySecretReference).HasMaxLength(255);
            entity.Property(host => host.PrivateKeyPassphraseSecretReference).HasMaxLength(255);
            entity.Property(host => host.Platform).HasMaxLength(128);
            entity.HasMany(host => host.SavedCommands)
                .WithOne(command => command.Host)
                .HasForeignKey(command => command.HostId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CloudflareSettingsEntity>(entity =>
        {
            entity.ToTable("cloudflare_settings");
            entity.HasKey(item => item.ManagedHostId);
            entity.Property(item => item.AccountId).HasMaxLength(64);
            entity.Property(item => item.AccountName).HasMaxLength(128);
            entity.Property(item => item.ZoneId).HasMaxLength(64);
            entity.Property(item => item.ZoneName).HasMaxLength(255);
            entity.Property(item => item.ApiTokenSecretReference).HasMaxLength(255);
            entity.HasOne<ManagedHostEntity>()
                .WithMany()
                .HasForeignKey(item => item.ManagedHostId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExposedServiceConfigEntity>(entity =>
        {
            entity.ToTable("exposed_service_configs");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.ServiceName).HasMaxLength(160);
            entity.Property(item => item.AccountId).HasMaxLength(64);
            entity.Property(item => item.AccountName).HasMaxLength(128);
            entity.Property(item => item.ZoneId).HasMaxLength(64);
            entity.Property(item => item.ZoneName).HasMaxLength(255);
            entity.Property(item => item.Hostname).HasMaxLength(255);
            entity.Property(item => item.LocalServiceUrl).HasMaxLength(512);
            entity.Property(item => item.TunnelId).HasMaxLength(64);
            entity.Property(item => item.TunnelName).HasMaxLength(160);
            entity.Property(item => item.DnsRecordId).HasMaxLength(64);
            entity.Property(item => item.AccessApplicationId).HasMaxLength(64);
            entity.Property(item => item.AccessPolicyId).HasMaxLength(64);
            entity.Property(item => item.AllowedEmailsJson).HasColumnType("TEXT");
            entity.Property(item => item.AllowedEmailDomainsJson).HasColumnType("TEXT");
            entity.Property(item => item.OriginRequestSettingsJson).HasColumnType("TEXT");
            entity.HasIndex(item => new { item.ManagedHostId, item.Hostname }).IsUnique();
            entity.HasOne<ManagedHostEntity>()
                .WithMany()
                .HasForeignKey(item => item.ManagedHostId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PortalConnectionSettingsEntity>(entity =>
        {
            entity.ToTable("portal_connection_settings");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.PortalBaseUrl).HasMaxLength(255);
            entity.Property(item => item.InstanceDisplayName).HasMaxLength(160);
            entity.Property(item => item.InstanceIdentityPrivateKeySecretReference).HasMaxLength(255);
            entity.Property(item => item.InstanceIdentityPublicKey).HasColumnType("TEXT");
            entity.Property(item => item.InstanceIdentityPublicKeyFingerprint).HasMaxLength(128);
            entity.Property(item => item.PairingCode).HasMaxLength(32);
            entity.Property(item => item.PortalOrganizationName).HasMaxLength(160);
            entity.Property(item => item.PortalApiKeyId).HasMaxLength(64);
            entity.Property(item => item.PortalApiSecretReference).HasMaxLength(255);
            entity.Property(item => item.LastConnectionStatus).HasMaxLength(512);
        });

        modelBuilder.Entity<LocalAiEngineSettingsEntity>(entity =>
        {
            entity.ToTable("local_ai_engine_settings");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.RuntimeEndpoint).HasMaxLength(512);
            entity.Property(item => item.DefaultModelId).HasMaxLength(160);
            entity.Property(item => item.LocalProviderKey).HasMaxLength(160);
            entity.Property(item => item.AllowedOrganizationIdsJson).HasColumnType("TEXT");
            entity.Property(item => item.AllowedInstanceIdsJson).HasColumnType("TEXT");
            entity.Property(item => item.AllowedModelIdsJson).HasColumnType("TEXT");
        });

        modelBuilder.Entity<LocalAiInstalledModelEntity>(entity =>
        {
            entity.ToTable("local_ai_installed_models");
            entity.HasKey(item => item.ModelId);
            entity.Property(item => item.ModelId).HasMaxLength(160);
            entity.Property(item => item.DisplayName).HasMaxLength(200);
            entity.Property(item => item.Digest).HasMaxLength(255);
            entity.Property(item => item.Detail).HasMaxLength(512);
        });

        modelBuilder.Entity<LocalAiHardwareSnapshotEntity>(entity =>
        {
            entity.ToTable("local_ai_hardware_snapshots");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.CpuModel).HasMaxLength(255);
            entity.Property(item => item.GpusJson).HasColumnType("TEXT");
            entity.Property(item => item.Summary).HasMaxLength(1024);
            entity.HasIndex(item => item.CapturedAtUtc);
        });

        modelBuilder.Entity<LocalAiBenchmarkResultEntity>(entity =>
        {
            entity.ToTable("local_ai_benchmark_results");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.ModelId).HasMaxLength(160);
            entity.Property(item => item.PromptSummary).HasMaxLength(255);
            entity.Property(item => item.Detail).HasColumnType("TEXT");
            entity.HasIndex(item => item.ExecutedAtUtc);
        });

        modelBuilder.Entity<LocalAiUsageEntryEntity>(entity =>
        {
            entity.ToTable("local_ai_usage_entries");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.ProviderKey).HasMaxLength(160);
            entity.Property(item => item.ConsumerOrganizationId).HasMaxLength(64);
            entity.Property(item => item.ConsumerInstanceId).HasMaxLength(64);
            entity.Property(item => item.ConsumerDisplayName).HasMaxLength(255);
            entity.Property(item => item.ModelId).HasMaxLength(160);
            entity.Property(item => item.ResultSummary).HasMaxLength(512);
            entity.HasIndex(item => item.CompletedAtUtc);
        });

        modelBuilder.Entity<LocalAiAuditEntryEntity>(entity =>
        {
            entity.ToTable("local_ai_audit_entries");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.EventType).HasMaxLength(120);
            entity.Property(item => item.Scope).HasMaxLength(120);
            entity.Property(item => item.Summary).HasMaxLength(512);
            entity.Property(item => item.Detail).HasColumnType("TEXT");
            entity.HasIndex(item => item.CreatedAtUtc);
        });

        modelBuilder.Entity<MediaLibrarySettingsEntity>(entity =>
        {
            entity.ToTable("media_library_settings");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.FfmpegPath).HasMaxLength(1024);
            entity.Property(item => item.FfprobePath).HasMaxLength(1024);
            entity.Property(item => item.TempCacheFolder).HasMaxLength(1024);
            entity.Property(item => item.IpAllowlistCsv).HasColumnType("TEXT");
        });

        modelBuilder.Entity<MediaLibraryRootEntity>(entity =>
        {
            entity.ToTable("media_library_roots");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(160);
            entity.Property(item => item.Path).HasMaxLength(1024);
            entity.Property(item => item.CustomCategoryName).HasMaxLength(160);
            entity.Property(item => item.IncludeExtensionsJson).HasColumnType("TEXT");
            entity.Property(item => item.ExcludeExtensionsJson).HasColumnType("TEXT");
            entity.Property(item => item.ExcludeFoldersJson).HasColumnType("TEXT");
            entity.Property(item => item.Notes).HasColumnType("TEXT");
            entity.Property(item => item.LastScanMessage).HasColumnType("TEXT");
            entity.HasIndex(item => item.Path).IsUnique();
            entity.HasIndex(item => new { item.Enabled, item.SortOrder });
            entity.HasMany(item => item.Items)
                .WithOne(item => item.LibraryRoot)
                .HasForeignKey(item => item.LibraryRootId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MediaItemEntity>(entity =>
        {
            entity.ToTable("media_items");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.RelativePath).HasMaxLength(2048);
            entity.Property(item => item.FullPath).HasMaxLength(4096);
            entity.Property(item => item.FileName).HasMaxLength(512);
            entity.Property(item => item.Extension).HasMaxLength(32);
            entity.Property(item => item.MimeType).HasMaxLength(160);
            entity.Property(item => item.VideoCodec).HasMaxLength(80);
            entity.Property(item => item.AudioCodec).HasMaxLength(80);
            entity.Property(item => item.Container).HasMaxLength(80);
            entity.HasIndex(item => item.FullPath).IsUnique();
            entity.HasIndex(item => new { item.LibraryRootId, item.RelativePath }).IsUnique();
            entity.HasIndex(item => new { item.MediaKind, item.Extension });
        });

        modelBuilder.Entity<SavedCommandEntity>(entity =>
        {
            entity.ToTable("saved_commands");
            entity.HasKey(command => command.Id);
            entity.Property(command => command.Name).HasMaxLength(128);
            entity.Property(command => command.IsQuickAccess).HasDefaultValue(false);
            entity.Property(command => command.IsGlobalFavorite).HasDefaultValue(false);
            entity.Property(command => command.IsTemplate).HasDefaultValue(false);
            entity.Property(command => command.ParameterDefinitionsJson).HasColumnType("TEXT");
            entity.Property(command => command.ParameterValueSnapshotJson).HasColumnType("TEXT");
        });

        modelBuilder.Entity<SambaShareEntity>(entity =>
        {
            entity.ToTable("samba_shares");
            entity.HasKey(share => share.Id);
            entity.Property(share => share.Name).HasMaxLength(128);
            entity.Property(share => share.SharePath).HasMaxLength(255);
            entity.Property(share => share.Description).HasMaxLength(512);
            entity.Property(share => share.ValidUsersJson).HasColumnType("TEXT");
            entity.Property(share => share.ValidGroupsJson).HasColumnType("TEXT");
            entity.Property(share => share.WriteListJson).HasColumnType("TEXT");
            entity.Property(share => share.ReadListJson).HasColumnType("TEXT");
            entity.Property(share => share.ForceUser).HasMaxLength(128);
            entity.Property(share => share.ForceGroup).HasMaxLength(128);
            entity.Property(share => share.CreateMask).HasMaxLength(16);
            entity.Property(share => share.DirectoryMask).HasMaxLength(16);
        });

        modelBuilder.Entity<LinuxServiceEntity>(entity =>
        {
            entity.ToTable("linux_services");
            entity.HasKey(service => service.Id);
            entity.Property(service => service.UnitName).HasMaxLength(128);
            entity.Property(service => service.DisplayName).HasMaxLength(128);
            entity.Property(service => service.HostName).HasMaxLength(128);
            entity.Property(service => service.Summary).HasMaxLength(512);
            entity.Property(service => service.RunningUser).HasMaxLength(128);
            entity.Property(service => service.RunningGroup).HasMaxLength(128);
            entity.Property(service => service.WorkingDirectory).HasMaxLength(255);
            entity.Property(service => service.ExecStart).HasMaxLength(512);
            entity.Property(service => service.EnvironmentFile).HasMaxLength(255);
            entity.Property(service => service.RecentErrorsJson).HasColumnType("TEXT");
        });

        modelBuilder.Entity<ScheduledTaskEntity>(entity =>
        {
            entity.ToTable("scheduled_tasks");
            entity.HasKey(task => task.Id);
            entity.Property(task => task.Name).HasMaxLength(160);
            entity.Property(task => task.Description).HasMaxLength(512);
            entity.Property(task => task.DaysOfWeekCsv).HasMaxLength(64);
            entity.Property(task => task.CustomCronExpression).HasColumnType("TEXT");
            entity.Property(task => task.CronExpression).HasMaxLength(128);
            entity.Property(task => task.ScheduleSummary).HasMaxLength(255);
            entity.Property(task => task.RunAsUser).HasMaxLength(128);
            entity.Property(task => task.WorkingDirectory).HasMaxLength(512);
            entity.Property(task => task.CommandText).HasColumnType("TEXT");
            entity.Property(task => task.ScriptPath).HasMaxLength(512);
            entity.Property(task => task.ScriptArguments).HasColumnType("TEXT");
            entity.Property(task => task.SourcePath).HasMaxLength(512);
            entity.Property(task => task.DestinationPath).HasMaxLength(512);
            entity.Property(task => task.MatchPatternsCsv).HasColumnType("TEXT");
            entity.Property(task => task.CommandPreview).HasColumnType("TEXT");
        });

        modelBuilder.Entity<CaddyProxyRouteEntity>(entity =>
        {
            entity.ToTable("caddy_proxy_routes");
            entity.HasKey(route => route.Id);
            entity.Property(route => route.Name).HasMaxLength(120);
            entity.Property(route => route.Hostname).HasMaxLength(255);
            entity.Property(route => route.UpstreamUrl).HasMaxLength(512);
            entity.Property(route => route.Description).HasMaxLength(320);
        });

        modelBuilder.Entity<EdgeGatewayRouteEntity>(entity =>
        {
            entity.ToTable("edge_gateway_routes");
            entity.HasKey(route => route.Id);
            entity.Property(route => route.DisplayName).HasMaxLength(160);
            entity.Property(route => route.Hostname).HasMaxLength(255);
            entity.Property(route => route.DomainName).HasMaxLength(255);
            entity.Property(route => route.TargetHost).HasMaxLength(255);
            entity.Property(route => route.TargetPathPrefix).HasMaxLength(255);
            entity.Property(route => route.AllowedUsers).HasColumnType("TEXT");
            entity.Property(route => route.AllowedGroups).HasColumnType("TEXT");
            entity.Property(route => route.AllowKnownIps).HasColumnType("TEXT");
            entity.Property(route => route.Notes).HasColumnType("TEXT");
            entity.Property(route => route.LastTestMessage).HasColumnType("TEXT");
            entity.HasIndex(route => route.Hostname).IsUnique();
            entity.HasIndex(route => route.DomainName);
            entity.HasIndex(route => route.Enabled);
        });

        modelBuilder.Entity<EdgeGatewayAuditEntryEntity>(entity =>
        {
            entity.ToTable("edge_gateway_audit_entries");
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.Hostname).HasMaxLength(255);
            entity.Property(entry => entry.RequestedPath).HasMaxLength(2048);
            entity.Property(entry => entry.SourceIp).HasMaxLength(96);
            entity.Property(entry => entry.UserEmail).HasMaxLength(255);
            entity.Property(entry => entry.Reason).HasMaxLength(512);
            entity.HasIndex(entry => entry.TimestampUtc);
            entity.HasIndex(entry => entry.Hostname);
            entity.HasIndex(entry => entry.UserEmail);
            entity.HasIndex(entry => entry.Decision);
        });

        modelBuilder.Entity<EdgeGatewaySettingsEntity>(entity =>
        {
            entity.ToTable("edge_gateway_settings");
            entity.HasKey(settings => settings.Id);
            entity.Property(settings => settings.GatewaySubdomain).HasMaxLength(63);
        });

        modelBuilder.Entity<MessagingEmailSettingsEntity>(entity =>
        {
            entity.ToTable("messaging_email_settings");
            entity.HasKey(settings => settings.Id);
            entity.Property(settings => settings.SenderAddress).HasMaxLength(255);
            entity.Property(settings => settings.SenderDisplayName).HasMaxLength(160);
            entity.Property(settings => settings.SmtpHost).HasMaxLength(255);
            entity.Property(settings => settings.SmtpUsername).HasMaxLength(255);
            entity.Property(settings => settings.SmtpPasswordSecretReference).HasMaxLength(255);
            entity.Property(settings => settings.GraphTenantId).HasMaxLength(128);
            entity.Property(settings => settings.GraphClientId).HasMaxLength(128);
            entity.Property(settings => settings.GraphClientSecretReference).HasMaxLength(255);
            entity.Property(settings => settings.GraphAuthority).HasMaxLength(255);
            entity.Property(settings => settings.GraphBaseUrl).HasMaxLength(255);
        });

        modelBuilder.Entity<SftpHostSettingsEntity>(entity =>
        {
            entity.ToTable("sftp_host_settings");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.BasePath).HasMaxLength(512);
            entity.Property(item => item.ManagedConfigPath).HasMaxLength(512);
        });

        modelBuilder.Entity<SftpManagedUserEntity>(entity =>
        {
            entity.ToTable("sftp_managed_users");
            entity.HasKey(item => item.UserName);
            entity.Property(item => item.UserName).HasMaxLength(64);
            entity.Property(item => item.LoginShell).HasMaxLength(255);
            entity.Property(item => item.PrimaryGroup).HasMaxLength(128);
            entity.Property(item => item.SupplementaryGroupsJson).HasColumnType("TEXT");
            entity.Property(item => item.BasePath).HasMaxLength(512);
            entity.Property(item => item.ChrootPath).HasMaxLength(512);
            entity.Property(item => item.WritablePath).HasMaxLength(512);
            entity.Property(item => item.ChrootOwner).HasMaxLength(128);
            entity.Property(item => item.ChrootGroup).HasMaxLength(128);
            entity.Property(item => item.ChrootMode).HasMaxLength(16);
            entity.Property(item => item.WritableOwner).HasMaxLength(128);
            entity.Property(item => item.WritableGroup).HasMaxLength(128);
            entity.Property(item => item.WritableMode).HasMaxLength(16);
            entity.HasMany(item => item.PublicKeys)
                .WithOne(item => item.User)
                .HasForeignKey(item => item.UserName)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SftpPublicKeyEntity>(entity =>
        {
            entity.ToTable("sftp_public_keys");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.UserName).HasMaxLength(64);
            entity.Property(item => item.Label).HasMaxLength(160);
            entity.Property(item => item.KeyType).HasMaxLength(64);
            entity.Property(item => item.Fingerprint).HasMaxLength(255);
            entity.Property(item => item.PublicKeyText).HasColumnType("TEXT");
        });

        modelBuilder.Entity<SftpAuditEntryEntity>(entity =>
        {
            entity.ToTable("sftp_audit_entries");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.EventType).HasMaxLength(80);
            entity.Property(item => item.TargetType).HasMaxLength(80);
            entity.Property(item => item.TargetKey).HasMaxLength(160);
            entity.Property(item => item.Summary).HasMaxLength(512);
            entity.Property(item => item.Details).HasColumnType("TEXT");
            entity.HasIndex(item => item.CreatedAtUtc);
            entity.HasIndex(item => new { item.TargetType, item.TargetKey });
        });

        modelBuilder.Entity<SftpBackupSnapshotEntity>(entity =>
        {
            entity.ToTable("sftp_backup_snapshots");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Summary).HasMaxLength(320);
            entity.Property(item => item.FilesJson).HasColumnType("TEXT");
            entity.Property(item => item.StorageDirectory).HasMaxLength(1024);
            entity.HasIndex(item => item.CreatedAtUtc);
        });

        modelBuilder.Entity<RemoteShareMountEntity>(entity =>
        {
            entity.ToTable("remote_share_mounts");
            entity.HasKey(mount => mount.Id);
            entity.Property(mount => mount.RemoteHost).HasMaxLength(255);
            entity.Property(mount => mount.RemoteAddress).HasMaxLength(128);
            entity.Property(mount => mount.ShareName).HasMaxLength(128);
            entity.Property(mount => mount.LocalMountPath).HasMaxLength(512);
            entity.Property(mount => mount.UserName).HasMaxLength(128);
            entity.Property(mount => mount.Domain).HasMaxLength(128);
            entity.Property(mount => mount.CredentialFilePath).HasMaxLength(512);
        });

        modelBuilder.Entity<LinuxShareUserEntity>(entity =>
        {
            entity.ToTable("linux_share_users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.UserName).HasMaxLength(128);
            entity.Property(user => user.DisplayName).HasMaxLength(128);
            entity.Property(user => user.PrimaryGroup).HasMaxLength(128);
            entity.Property(user => user.SupplementaryGroupsJson).HasColumnType("TEXT");
            entity.Property(user => user.HomeDirectory).HasMaxLength(255);
            entity.Property(user => user.LoginShell).HasMaxLength(128);
        });

        modelBuilder.Entity<LocalUserAccessPolicyEntity>(entity =>
        {
            entity.ToTable("local_user_access_policies");
            entity.HasKey(policy => policy.UserName);
            entity.Property(policy => policy.UserName).HasMaxLength(128);
            entity.Property(policy => policy.AuthorizedKeyEntries).HasColumnType("TEXT");
        });

        modelBuilder.Entity<LinuxShareGroupEntity>(entity =>
        {
            entity.ToTable("linux_share_groups");
            entity.HasKey(group => group.Id);
            entity.Property(group => group.GroupName).HasMaxLength(128);
            entity.Property(group => group.Description).HasMaxLength(512);
            entity.Property(group => group.MembersJson).HasColumnType("TEXT");
        });

        modelBuilder.Entity<AiChatThreadEntity>(entity =>
        {
            entity.ToTable("ai_chat_threads");
            entity.HasKey(thread => thread.Id);
            entity.Property(thread => thread.Title).HasMaxLength(160);
            entity.Property(thread => thread.ProviderKey).HasMaxLength(80);
            entity.Property(thread => thread.ModelId).HasMaxLength(128);
            entity.Property(thread => thread.ProviderConversationReference).HasMaxLength(255);
            entity.Property(thread => thread.ProviderStateReference).HasMaxLength(255);
            entity.HasIndex(thread => thread.UpdatedAtUtc);
            entity.HasMany(thread => thread.Messages)
                .WithOne(message => message.Thread)
                .HasForeignKey(message => message.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(thread => thread.AttachedServers)
                .WithOne(server => server.Thread)
                .HasForeignKey(server => server.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(thread => thread.ExecutionPlans)
                .WithOne(plan => plan.Thread)
                .HasForeignKey(plan => plan.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(thread => thread.ApprovalRequests)
                .WithOne(request => request.Thread)
                .HasForeignKey(request => request.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(thread => thread.ToolInvocations)
                .WithOne(invocation => invocation.Thread)
                .HasForeignKey(invocation => invocation.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(thread => thread.ChatRuns)
                .WithOne(run => run.Thread)
                .HasForeignKey(run => run.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(thread => thread.AuditEntries)
                .WithOne(entry => entry.Thread)
                .HasForeignKey(entry => entry.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(thread => thread.Checkpoints)
                .WithOne(checkpoint => checkpoint.Thread)
                .HasForeignKey(checkpoint => checkpoint.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiChatMessageEntity>(entity =>
        {
            entity.ToTable("ai_chat_messages");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Content).HasColumnType("TEXT");
            entity.HasIndex(message => new { message.ThreadId, message.SequenceNumber }).IsUnique();
        });

        modelBuilder.Entity<AiAttachedServerEntity>(entity =>
        {
            entity.ToTable("ai_attached_servers");
            entity.HasKey(server => server.Id);
            entity.Property(server => server.ServerName).HasMaxLength(160);
            entity.Property(server => server.Hostname).HasMaxLength(255);
            entity.Property(server => server.Environment).HasMaxLength(64);
            entity.HasIndex(server => new { server.ThreadId, server.ManagedHostId }).IsUnique();
            entity.HasOne(server => server.ManagedHost)
                .WithMany()
                .HasForeignKey(server => server.ManagedHostId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AiExecutionPlanEntity>(entity =>
        {
            entity.ToTable("ai_execution_plans");
            entity.HasKey(plan => plan.Id);
            entity.Property(plan => plan.Summary).HasMaxLength(512);
            entity.HasIndex(plan => new { plan.ThreadId, plan.CreatedAtUtc });
            entity.HasMany(plan => plan.ProposedActions)
                .WithOne(action => action.ExecutionPlan)
                .HasForeignKey(action => action.ExecutionPlanId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiProposedActionEntity>(entity =>
        {
            entity.ToTable("ai_proposed_actions");
            entity.HasKey(action => action.Id);
            entity.Property(action => action.Title).HasMaxLength(160);
            entity.Property(action => action.Description).HasColumnType("TEXT");
            entity.Property(action => action.ToolName).HasMaxLength(128);
            entity.Property(action => action.ProviderToolCallId).HasMaxLength(255);
            entity.Property(action => action.ToolArgumentsJson).HasColumnType("TEXT");
            entity.Property(action => action.CommandPreview).HasColumnType("TEXT");
            entity.Property(action => action.PolicyReason).HasColumnType("TEXT");
            entity.Property(action => action.SafeChangeJson).HasColumnType("TEXT");
            entity.HasIndex(action => new { action.ExecutionPlanId, action.SequenceNumber }).IsUnique();
        });

        modelBuilder.Entity<AiApprovalRequestEntity>(entity =>
        {
            entity.ToTable("ai_approval_requests");
            entity.HasKey(request => request.Id);
            entity.Property(request => request.Title).HasMaxLength(160);
            entity.Property(request => request.Summary).HasColumnType("TEXT");
            entity.Property(request => request.ToolName).HasMaxLength(128);
            entity.Property(request => request.CommandPreview).HasColumnType("TEXT");
            entity.Property(request => request.PolicyReason).HasColumnType("TEXT");
            entity.HasIndex(request => new { request.ThreadId, request.State });
            entity.HasOne(request => request.Decision)
                .WithOne(decision => decision.ApprovalRequest)
                .HasForeignKey<AiApprovalDecisionEntity>(decision => decision.ApprovalRequestId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiApprovalDecisionEntity>(entity =>
        {
            entity.ToTable("ai_approval_decisions");
            entity.HasKey(decision => decision.ApprovalRequestId);
            entity.Property(decision => decision.DecidedBy).HasMaxLength(128);
            entity.Property(decision => decision.Reason).HasColumnType("TEXT");
        });

        modelBuilder.Entity<AiToolInvocationEntity>(entity =>
        {
            entity.ToTable("ai_tool_invocations");
            entity.HasKey(invocation => invocation.Id);
            entity.Property(invocation => invocation.ToolName).HasMaxLength(128);
            entity.Property(invocation => invocation.ArgumentsJson).HasColumnType("TEXT");
            entity.HasIndex(invocation => invocation.ProposedActionId).IsUnique();
            entity.HasIndex(invocation => new { invocation.ThreadId, invocation.StartedAtUtc });
            entity.HasOne(invocation => invocation.Result)
                .WithOne(result => result.Invocation)
                .HasForeignKey<AiToolResultEntity>(result => result.InvocationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiChatRunEntity>(entity =>
        {
            entity.ToTable("ai_chat_runs");
            entity.HasKey(run => run.Id);
            entity.Property(run => run.StatusSummary).HasMaxLength(256);
            entity.Property(run => run.CurrentProviderResponseId).HasMaxLength(255);
            entity.Property(run => run.PendingAssistantOutputsJson).HasColumnType("TEXT");
            entity.Property(run => run.PendingToolCallsJson).HasColumnType("TEXT");
            entity.Property(run => run.LastError).HasColumnType("TEXT");
            entity.HasIndex(run => new { run.ThreadId, run.UpdatedAtUtc });
            entity.HasIndex(run => new { run.ThreadId, run.Status });
            entity.HasIndex(run => run.ExecutionPlanId);
            entity.HasIndex(run => run.MessageId).IsUnique();
        });

        modelBuilder.Entity<AiToolResultEntity>(entity =>
        {
            entity.ToTable("ai_tool_results");
            entity.HasKey(result => result.Id);
            entity.Property(result => result.Summary).HasMaxLength(256);
            entity.Property(result => result.OutputText).HasColumnType("TEXT");
            entity.Property(result => result.ErrorText).HasColumnType("TEXT");
            entity.Property(result => result.PayloadJson).HasColumnType("TEXT");
            entity.HasIndex(result => result.InvocationId).IsUnique();
        });

        modelBuilder.Entity<AiAuditEntryEntity>(entity =>
        {
            entity.ToTable("ai_audit_entries");
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.EventType).HasMaxLength(96);
            entity.Property(entry => entry.Summary).HasMaxLength(256);
            entity.Property(entry => entry.Details).HasColumnType("TEXT");
            entity.Property(entry => entry.MetadataJson).HasColumnType("TEXT");
            entity.HasIndex(entry => new { entry.ThreadId, entry.CreatedAtUtc });
        });

        modelBuilder.Entity<AiChatCheckpointEntity>(entity =>
        {
            entity.ToTable("ai_chat_checkpoints");
            entity.HasKey(checkpoint => checkpoint.Id);
            entity.Property(checkpoint => checkpoint.Label).HasMaxLength(128);
            entity.Property(checkpoint => checkpoint.Summary).HasMaxLength(256);
            entity.Property(checkpoint => checkpoint.StateJson).HasColumnType("TEXT");
            entity.HasIndex(checkpoint => new { checkpoint.ThreadId, checkpoint.CreatedAtUtc });
        });

        modelBuilder.Entity<AiProviderSettingsEntity>(entity =>
        {
            entity.ToTable("ai_provider_settings");
            entity.HasKey(provider => provider.ProviderKey);
            entity.Property(provider => provider.ProviderKey).HasMaxLength(80);
            entity.Property(provider => provider.DisplayName).HasMaxLength(120);
            entity.Property(provider => provider.BaseUrl).HasMaxLength(255);
            entity.Property(provider => provider.DefaultModelId).HasMaxLength(128);
            entity.Property(provider => provider.ApiKeySecretReference).HasMaxLength(255);
            entity.Property(provider => provider.Notes).HasColumnType("TEXT");
            entity.Property(provider => provider.MetadataJson).HasColumnType("TEXT");
            entity.HasIndex(provider => provider.IsDefault);
            entity.HasIndex(provider => new { provider.ProviderType, provider.IsEnabled });
        });

        modelBuilder.Entity<UserDisplayPreferenceEntity>(entity =>
        {
            entity.ToTable("user_display_preferences");
            entity.HasKey(item => item.UserId);
            entity.Property(item => item.ThemePaletteId).HasMaxLength(64);
            entity.Property(item => item.ThemeMode).HasMaxLength(16);
        });

        modelBuilder.Entity<FileBrowserShortcutEntity>(entity =>
        {
            entity.ToTable("file_browser_shortcuts");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Label).HasMaxLength(128);
            entity.Property(item => item.TargetPath).HasMaxLength(1024);
            entity.HasIndex(item => new { item.UserId, item.ManagedHostId, item.SortOrder });
            entity.HasIndex(item => new { item.UserId, item.ManagedHostId, item.TargetPath }).IsUnique();
        });

        modelBuilder.Entity<ProtectedSecretEntity>(entity =>
        {
            entity.ToTable("protected_secrets");
            entity.HasKey(secret => secret.Reference);
            entity.Property(secret => secret.Reference).HasMaxLength(255);
            entity.Property(secret => secret.Purpose).HasMaxLength(128);
            entity.Property(secret => secret.ProtectedValue).HasColumnType("TEXT");
        });

        modelBuilder.Entity<SecurityUserEntity>(entity =>
        {
            entity.ToTable("security_users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.Email).HasMaxLength(255);
            entity.Property(user => user.NormalizedEmail).HasMaxLength(255);
            entity.Property(user => user.LinuxUsername).HasMaxLength(64);
            entity.Property(user => user.SessionLifetimeMinutes).HasDefaultValue(SecuritySessionPolicy.DefaultSessionLifetimeMinutes);
            entity.Property(user => user.AuthorizedKeyEntries).HasColumnType("TEXT");
            entity.Property(user => user.OtpSecretReference).HasMaxLength(255);
            entity.HasIndex(user => user.NormalizedEmail).IsUnique();
        });

        modelBuilder.Entity<SecurityPasskeyCredentialEntity>(entity =>
        {
            entity.ToTable("security_passkey_credentials");
            entity.HasKey(credential => credential.Id);
            entity.Property(credential => credential.CredentialId).HasMaxLength(1024);
            entity.Property(credential => credential.PublicKey).HasColumnType("TEXT");
            entity.Property(credential => credential.UserHandle).HasMaxLength(256);
            entity.Property(credential => credential.FriendlyName).HasMaxLength(160);
            entity.HasIndex(credential => credential.CredentialId).IsUnique();
            entity.HasIndex(credential => credential.UserId);
            entity.HasOne(credential => credential.User)
                .WithMany()
                .HasForeignKey(credential => credential.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LocalInstanceIdentityEntity>(entity =>
        {
            entity.ToTable("local_instance_identity");
            entity.HasKey(identity => identity.Id);
            entity.Property(identity => identity.DisplayName).HasMaxLength(160);
            entity.Property(identity => identity.PrivateKeySecretReference).HasMaxLength(255);
            entity.Property(identity => identity.PublicKey).HasColumnType("TEXT");
            entity.Property(identity => identity.PublicKeyFingerprint).HasMaxLength(128);
        });

        modelBuilder.Entity<TrustedNetworkEntryEntity>(entity =>
        {
            entity.ToTable("trusted_network_entries");
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.Label).HasMaxLength(128);
            entity.Property(entry => entry.AddressOrCidr).HasMaxLength(96);
            entity.Property(entry => entry.Description).HasMaxLength(512);
            entity.HasIndex(entry => entry.AddressOrCidr).IsUnique();
            entity.HasIndex(entry => new { entry.IsEnabled, entry.IsBuiltIn });
        });
    }
}
