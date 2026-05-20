// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Infrastructure.Persistence.Seed;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Data;

namespace LinuxMadeSane.Infrastructure.Persistence;

public sealed class SqliteDatabaseInitializer(
    LinuxMadeSaneDbContext dbContext,
    IConfiguration? configuration = null,
    ISecretStore? secretStore = null)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        EnsureDatabaseDirectoryExists();
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureManagedHostColumnsAsync(cancellationToken);
        await EnsureSavedCommandColumnsAsync(cancellationToken);
        await EnsureModuleTablesAsync(cancellationToken);
        await EnsureAiTablesAsync(cancellationToken);
        await EnsureLocalAiTablesAsync(cancellationToken);
        await EnsureSecurityTablesAsync(cancellationToken);
        await EnsureMessagingTablesAsync(cancellationToken);
        await EnsureCloudflareTablesAsync(cancellationToken);
        await EnsurePortalTablesAsync(cancellationToken);
        await EnsureCaddyTablesAsync(cancellationToken);
        await EnsureEdgeGatewayTablesAsync(cancellationToken);
        await EnsureMediaLibraryTablesAsync(cancellationToken);
        await EnsureSftpTablesAsync(cancellationToken);
        await EnsureUserDisplayPreferenceTablesAsync(cancellationToken);
        await EnsureFileBrowserShortcutTablesAsync(cancellationToken);
        await RemoveLegacyScaffoldDataAsync(cancellationToken);

        if (!await dbContext.ManagedHosts.AnyAsync(cancellationToken))
        {
            dbContext.ManagedHosts.AddRange(SqliteSeedData.ManagedHosts);
        }

        if (!await dbContext.SavedCommands.AnyAsync(cancellationToken))
        {
            dbContext.SavedCommands.AddRange(SqliteSeedData.SavedCommands);
        }

        if (!await dbContext.SambaShares.AnyAsync(cancellationToken))
        {
            dbContext.SambaShares.AddRange(SqliteSeedData.SambaShares);
        }

        if (!await dbContext.LinuxServices.AnyAsync(cancellationToken))
        {
            dbContext.LinuxServices.AddRange(SqliteSeedData.LinuxServices);
        }

        await EnsureLocalManagedHostAsync(cancellationToken);
        await EnsureLocalManagedHostBootstrapAsync(cancellationToken);
        await EnsureBuiltInTrustedNetworksAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task RemoveLegacyScaffoldDataAsync(CancellationToken cancellationToken)
    {
        var scaffoldHostIds = new[]
        {
            Guid.Parse("6ec4bb76-3f65-454b-b948-fc013d1d5f0c"),
            Guid.Parse("e2cb4fab-99e6-4310-bbec-7666f26fd4a7")
        };

        var scaffoldCommandIds = new[]
        {
            Guid.Parse("9c50b7aa-8d5d-49a9-ad2d-c9cf719ae5d5"),
            Guid.Parse("3cebbbd6-992f-47e7-b9ad-fe1832412b34"),
            Guid.Parse("9b6431cc-6187-49ad-a59f-b0a3f5fe1d95")
        };

        var scaffoldShareIds = new[]
        {
            Guid.Parse("7b08ab9c-9c22-4fa2-ba7b-4fec0604b664"),
            Guid.Parse("9b09b3e2-9d53-4e7b-95db-b2b861c6f5a1"),
            Guid.Parse("08a42fbb-b04e-4d19-9638-089e4c58f0c0")
        };

        var scaffoldServiceIds = new[]
        {
            Guid.Parse("68e51e78-1b4a-46d7-89a9-d0d4c3bbd620"),
            Guid.Parse("1e370ef8-430d-4d26-9ea7-2f6dbdb58ca5"),
            Guid.Parse("af743b06-7632-432e-951e-586ffddfaaf0")
        };

        var scaffoldShares = await dbContext.SambaShares
            .Where(item => scaffoldShareIds.Contains(item.Id))
            .ToListAsync(cancellationToken);

        var scaffoldHosts = await dbContext.ManagedHosts
            .Where(item => scaffoldHostIds.Contains(item.Id))
            .ToListAsync(cancellationToken);

        var scaffoldCommands = await dbContext.SavedCommands
            .Where(item => scaffoldCommandIds.Contains(item.Id))
            .ToListAsync(cancellationToken);

        var scaffoldServices = await dbContext.LinuxServices
            .Where(item => scaffoldServiceIds.Contains(item.Id))
            .ToListAsync(cancellationToken);

        if (scaffoldCommands.Count > 0)
        {
            dbContext.SavedCommands.RemoveRange(scaffoldCommands);
        }

        if (scaffoldHosts.Count > 0)
        {
            dbContext.ManagedHosts.RemoveRange(scaffoldHosts);
        }

        if (scaffoldShares.Count > 0)
        {
            dbContext.SambaShares.RemoveRange(scaffoldShares);
        }

        if (scaffoldServices.Count > 0)
        {
            dbContext.LinuxServices.RemoveRange(scaffoldServices);
        }
    }

    private async Task EnsureLocalManagedHostAsync(CancellationToken cancellationToken)
    {
        var localHost = AiLocalMachine.CreateManagedHost();
        var entity = dbContext.ManagedHosts.Local.SingleOrDefault(host => host.Id == AiLocalMachine.ManagedHostId)
            ?? await dbContext.ManagedHosts.SingleOrDefaultAsync(
                host => host.Id == AiLocalMachine.ManagedHostId,
                cancellationToken);

        if (entity is null)
        {
            dbContext.ManagedHosts.Add(MapLocalManagedHost(localHost));
            return;
        }

        entity.Name = localHost.Name;
        entity.Hostname = localHost.Hostname;
        entity.Port = entity.Port > 0 ? entity.Port : localHost.Port;
        entity.Environment = localHost.Environment;
        entity.Description = localHost.Description;
        entity.DefaultWorkingDirectory = string.IsNullOrWhiteSpace(entity.DefaultWorkingDirectory)
            ? localHost.DefaultWorkingDirectory
            : entity.DefaultWorkingDirectory;
        entity.OperatingStatus = (int)localHost.OperatingStatus;
        entity.PrimaryAuthenticationType = entity.PrimaryAuthenticationType == default
            ? (int)localHost.PrimaryAuthenticationType
            : entity.PrimaryAuthenticationType;
        entity.Username = string.IsNullOrWhiteSpace(entity.Username) ? localHost.Username : entity.Username;
        entity.LastSeenUtc = localHost.LastSeenUtc;
        entity.LastConnectionTestStatus = (int)localHost.LastConnectionTestStatus;
        entity.Platform = localHost.Platform;
        entity.Kind = (int)localHost.Kind;
    }

    private async Task EnsureLocalManagedHostBootstrapAsync(CancellationToken cancellationToken)
    {
        var bootstrap = ReadLocalManagedHostBootstrap();
        if (!bootstrap.Enabled || secretStore is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(bootstrap.Username) ||
            string.IsNullOrWhiteSpace(bootstrap.PrivateKeyPath) ||
            !File.Exists(bootstrap.PrivateKeyPath))
        {
            return;
        }

        var entity = dbContext.ManagedHosts.Local.SingleOrDefault(host => host.Id == AiLocalMachine.ManagedHostId)
            ?? await dbContext.ManagedHosts.SingleOrDefaultAsync(
                host => host.Id == AiLocalMachine.ManagedHostId,
                cancellationToken);
        if (entity is null)
        {
            entity = MapLocalManagedHost(AiLocalMachine.CreateManagedHost());
            dbContext.ManagedHosts.Add(entity);
        }

        if (!ShouldApplyLocalManagedHostBootstrap(entity, bootstrap))
        {
            return;
        }

        var privateKey = await File.ReadAllTextAsync(bootstrap.PrivateKeyPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(privateKey))
        {
            return;
        }

        await ReplaceLocalManagedHostPrivateKeyIfNeededAsync(entity, privateKey, cancellationToken);

        var existingPasswordSecretReference = entity.PasswordSecretReference;
        var existingPassphraseSecretReference = entity.PrivateKeyPassphraseSecretReference;
        entity.Name = AiLocalMachine.Name;
        entity.Hostname = AiLocalMachine.Hostname;
        entity.Port = bootstrap.Port;
        entity.Environment = AiLocalMachine.EnvironmentLabel;
        entity.Description = AiLocalMachine.Description;
        entity.DefaultWorkingDirectory = bootstrap.DefaultWorkingDirectory;
        entity.PrimaryAuthenticationType = (int)AuthenticationType.PrivateKey;
        entity.FallbackAuthenticationType = null;
        entity.Username = bootstrap.Username;
        entity.PasswordSecretReference = null;
        entity.PrivateKeyPassphraseSecretReference = null;
        entity.UseKeyboardInteractiveFallback = false;
        entity.LastConnectionTestStatus = (int)ConnectionTestStatus.Succeeded;
        entity.Kind = (int)ManagedHostKind.LmsHost;

        await DeleteSecretIfPresentAsync(existingPasswordSecretReference, cancellationToken);
        await DeleteSecretIfPresentAsync(existingPassphraseSecretReference, cancellationToken);
    }

    private LocalManagedHostBootstrap ReadLocalManagedHostBootstrap()
    {
        if (configuration is null)
        {
            return LocalManagedHostBootstrap.Disabled;
        }

        var section = configuration.GetSection("LocalHostBootstrap");
        var privateKeyPath = section["PrivateKeyPath"]?.Trim() ?? string.Empty;
        var username = section["Username"]?.Trim() ?? string.Empty;
        var defaultWorkingDirectory = section["DefaultWorkingDirectory"]?.Trim();
        var portValue = section["Port"];

        var enabled = ReadBoolean(section["Enabled"], !string.IsNullOrWhiteSpace(privateKeyPath));
        if (!enabled)
        {
            return LocalManagedHostBootstrap.Disabled;
        }

        if (!int.TryParse(portValue, out var port) || port is < 1 or > 65535)
        {
            port = AiLocalMachine.DefaultPort;
        }

        if (!string.IsNullOrWhiteSpace(privateKeyPath) && !Path.IsPathRooted(privateKeyPath))
        {
            privateKeyPath = Path.GetFullPath(privateKeyPath);
        }

        if (string.IsNullOrWhiteSpace(defaultWorkingDirectory))
        {
            defaultWorkingDirectory = username.Length == 0
                ? "/home"
                : $"/home/{username}";
        }

        return new LocalManagedHostBootstrap(
            enabled,
            username,
            privateKeyPath,
            port,
            defaultWorkingDirectory,
            ReadBoolean(section["ForceUpdate"], false));
    }

    private static bool ShouldApplyLocalManagedHostBootstrap(
        ManagedHostEntity entity,
        LocalManagedHostBootstrap bootstrap)
    {
        if (bootstrap.ForceUpdate)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(entity.PrivateKeySecretReference))
        {
            return true;
        }

        return IsInstallerManagedLocalUsername(entity.Username, bootstrap.Username);
    }

    private static bool IsInstallerManagedLocalUsername(string? username, string bootstrapUsername)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return true;
        }

        var trimmed = username.Trim();
        return trimmed.Equals(bootstrapUsername, StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("linuxmadesane", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ReplaceLocalManagedHostPrivateKeyIfNeededAsync(
        ManagedHostEntity entity,
        string privateKey,
        CancellationToken cancellationToken)
    {
        var existingPrivateKeySecretReference = entity.PrivateKeySecretReference;
        var existingPrivateKey = await TryResolveSecretAsync(existingPrivateKeySecretReference, cancellationToken);
        if (!string.IsNullOrWhiteSpace(existingPrivateKey) &&
            string.Equals(existingPrivateKey.Trim(), privateKey.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        entity.PrivateKeySecretReference = await secretStore!.StoreSecretAsync(
            privateKey,
            "local-managed-host-private-key",
            cancellationToken);

        await DeleteSecretIfPresentAsync(existingPrivateKeySecretReference, cancellationToken);
    }

    private async Task<string?> TryResolveSecretAsync(string? secretReference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secretReference))
        {
            return null;
        }

        try
        {
            return await secretStore!.ResolveSecretAsync(secretReference, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task DeleteSecretIfPresentAsync(string? secretReference, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(secretReference))
        {
            await secretStore!.DeleteSecretAsync(secretReference, cancellationToken);
        }
    }

    private static bool ReadBoolean(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Trim() switch
        {
            "1" => true,
            "0" => false,
            var text when text.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            var text when text.Equals("yes", StringComparison.OrdinalIgnoreCase) => true,
            var text when text.Equals("on", StringComparison.OrdinalIgnoreCase) => true,
            var text when text.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            var text when text.Equals("no", StringComparison.OrdinalIgnoreCase) => false,
            var text when text.Equals("off", StringComparison.OrdinalIgnoreCase) => false,
            _ => defaultValue
        };
    }

    private async Task EnsureSavedCommandColumnsAsync(CancellationToken cancellationToken)
    {
        await using var connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('saved_commands');";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (!columns.Contains("IsQuickAccess"))
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE saved_commands ADD COLUMN IsQuickAccess INTEGER NOT NULL DEFAULT 0;",
                cancellationToken);
        }

        if (!columns.Contains("IsGlobalFavorite"))
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE saved_commands ADD COLUMN IsGlobalFavorite INTEGER NOT NULL DEFAULT 0;",
                cancellationToken);
        }

        if (!columns.Contains("IsTemplate"))
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE saved_commands ADD COLUMN IsTemplate INTEGER NOT NULL DEFAULT 0;",
                cancellationToken);
        }

        if (!columns.Contains("TemplateSourceId"))
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE saved_commands ADD COLUMN TemplateSourceId TEXT NULL;",
                cancellationToken);
        }

        if (!columns.Contains("LinkGroupId"))
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE saved_commands ADD COLUMN LinkGroupId TEXT NULL;",
                cancellationToken);
        }

        if (!columns.Contains("ParameterDefinitionsJson"))
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE saved_commands ADD COLUMN ParameterDefinitionsJson TEXT NOT NULL DEFAULT '[]';",
                cancellationToken);
        }

        if (!columns.Contains("ParameterValueSnapshotJson"))
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE saved_commands ADD COLUMN ParameterValueSnapshotJson TEXT NOT NULL DEFAULT '{{}}';",
                cancellationToken);
        }
    }

    private async Task EnsureModuleTablesAsync(CancellationToken cancellationToken)
    {
        const string shareSql = """
            CREATE TABLE IF NOT EXISTS samba_shares (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                SharePath TEXT NOT NULL,
                Description TEXT NOT NULL,
                Browseable INTEGER NOT NULL,
                ReadOnly INTEGER NOT NULL,
                GuestAccess INTEGER NOT NULL,
                ValidUsersJson TEXT NOT NULL,
                ValidGroupsJson TEXT NOT NULL,
                WriteListJson TEXT NOT NULL,
                ReadListJson TEXT NOT NULL,
                ForceUser TEXT NULL,
                ForceGroup TEXT NULL,
                CreateMask TEXT NOT NULL,
                DirectoryMask TEXT NOT NULL,
                CreateMaskExplanation TEXT NOT NULL,
                DirectoryMaskExplanation TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(shareSql, cancellationToken);

        const string usersSql = """
            CREATE TABLE IF NOT EXISTS linux_share_users (
                Id TEXT NOT NULL PRIMARY KEY,
                UserName TEXT NOT NULL,
                DisplayName TEXT NOT NULL,
                PrimaryGroup TEXT NOT NULL,
                SupplementaryGroupsJson TEXT NOT NULL,
                HomeDirectory TEXT NOT NULL,
                LoginShell TEXT NOT NULL,
                IsEnabled INTEGER NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(usersSql, cancellationToken);

        const string groupsSql = """
            CREATE TABLE IF NOT EXISTS linux_share_groups (
                Id TEXT NOT NULL PRIMARY KEY,
                GroupName TEXT NOT NULL,
                Description TEXT NOT NULL,
                MembersJson TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(groupsSql, cancellationToken);

        const string serviceSql = """
            CREATE TABLE IF NOT EXISTS linux_services (
                Id TEXT NOT NULL PRIMARY KEY,
                UnitName TEXT NOT NULL,
                DisplayName TEXT NOT NULL,
                HostName TEXT NOT NULL,
                Summary TEXT NOT NULL,
                RuntimeState INTEGER NOT NULL,
                HealthStatus INTEGER NOT NULL,
                EnabledAtBoot INTEGER NOT NULL,
                ActiveUnderSystemd INTEGER NOT NULL,
                RunningUser TEXT NOT NULL,
                RunningGroup TEXT NOT NULL,
                WorkingDirectory TEXT NOT NULL,
                ExecStart TEXT NOT NULL,
                EnvironmentFile TEXT NULL,
                RestartCount INTEGER NOT NULL,
                LastStartTime TEXT NOT NULL,
                ListeningPort INTEGER NOT NULL,
                RecentErrorsJson TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(serviceSql, cancellationToken);

        const string scheduledTasksSql = """
            CREATE TABLE IF NOT EXISTS scheduled_tasks (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                Description TEXT NOT NULL,
                IsEnabled INTEGER NOT NULL,
                TaskKind INTEGER NOT NULL,
                ScheduleMode INTEGER NOT NULL,
                Minute INTEGER NOT NULL,
                Hour INTEGER NOT NULL,
                DayOfMonth INTEGER NOT NULL,
                DaysOfWeekCsv TEXT NOT NULL,
                CustomCronExpression TEXT NOT NULL,
                CronExpression TEXT NOT NULL,
                ScheduleSummary TEXT NOT NULL,
                RunAsUser TEXT NOT NULL,
                WorkingDirectory TEXT NOT NULL,
                RunbookId TEXT NULL,
                ExecutionToken TEXT NOT NULL DEFAULT '',
                CommandText TEXT NOT NULL,
                ScriptPath TEXT NOT NULL,
                ScriptArguments TEXT NOT NULL,
                SourcePath TEXT NOT NULL,
                DestinationPath TEXT NOT NULL,
                CopyRecursive INTEGER NOT NULL,
                CopyPreserveAttributes INTEGER NOT NULL,
                CopyDeleteSourceAfterCopy INTEGER NOT NULL,
                MatchPatternsCsv TEXT NOT NULL,
                MatchCaseInsensitive INTEGER NOT NULL,
                AgeFilterMode INTEGER NOT NULL,
                AgeFilterValue INTEGER NOT NULL,
                AgeFilterUnit INTEGER NOT NULL,
                CleanupDeleteFiles INTEGER NOT NULL,
                CleanupDeleteDirectories INTEGER NOT NULL,
                UpdatePackageLists INTEGER NOT NULL,
                UpgradeInstalledPackages INTEGER NOT NULL,
                RemoveUnusedPackages INTEGER NOT NULL,
                CommandPreview TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(scheduledTasksSql, cancellationToken);
        await EnsureScheduledTaskColumnsAsync(cancellationToken);

        const string remoteShareMountsSql = """
            CREATE TABLE IF NOT EXISTS remote_share_mounts (
                Id TEXT NOT NULL PRIMARY KEY,
                RemoteHost TEXT NOT NULL,
                RemoteAddress TEXT NULL,
                ShareName TEXT NOT NULL,
                LocalMountPath TEXT NOT NULL,
                UserName TEXT NULL,
                Domain TEXT NULL,
                CredentialFilePath TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                LastMountedAtUtc TEXT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(remoteShareMountsSql, cancellationToken);

        const string sshfsMountsSql = """
            CREATE TABLE IF NOT EXISTS sshfs_mounts (
                Id TEXT NOT NULL PRIMARY KEY,
                HostId TEXT NOT NULL,
                HostDisplayName TEXT NOT NULL,
                HostAddress TEXT NOT NULL,
                Port INTEGER NOT NULL,
                UserName TEXT NOT NULL,
                RemotePath TEXT NOT NULL,
                LocalMountPath TEXT NOT NULL,
                IdentityFilePath TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                LastMountedAtUtc TEXT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(sshfsMountsSql, cancellationToken);
    }

    private async Task EnsurePortalTablesAsync(CancellationToken cancellationToken)
    {
        const string portalConnectionSettingsSql = """
            CREATE TABLE IF NOT EXISTS portal_connection_settings (
                Id INTEGER NOT NULL PRIMARY KEY,
                LocalInstanceId TEXT NOT NULL,
                PortalBaseUrl TEXT NOT NULL,
                InstanceDisplayName TEXT NOT NULL,
                IsEnabled INTEGER NOT NULL,
                InstanceIdentityPrivateKeySecretReference TEXT NULL,
                InstanceIdentityPublicKey TEXT NOT NULL DEFAULT '',
                InstanceIdentityPublicKeyFingerprint TEXT NOT NULL DEFAULT '',
                PairingCode TEXT NOT NULL,
                PairingCodeGeneratedAtUtc TEXT NULL,
                PairingCodeExpiresAtUtc TEXT NULL,
                PortalOrganizationId TEXT NULL,
                PortalOrganizationName TEXT NULL,
                PortalInstanceId TEXT NULL,
                PortalApiKeyId TEXT NULL,
                PortalApiSecretReference TEXT NULL,
                LastConnectionStatus TEXT NOT NULL,
                LastConnectedAtUtc TEXT NULL,
                LastHeartbeatAtUtc TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(portalConnectionSettingsSql, cancellationToken);
        await EnsurePortalConnectionSettingsCompatibilityAsync(cancellationToken);
    }

    private async Task EnsureCaddyTablesAsync(CancellationToken cancellationToken)
    {
        const string caddyRoutesSql = """
            CREATE TABLE IF NOT EXISTS caddy_proxy_routes (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                Hostname TEXT NOT NULL,
                UpstreamUrl TEXT NOT NULL,
                Description TEXT NOT NULL,
                EnableTls INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(caddyRoutesSql, cancellationToken);
    }

    private async Task EnsureEdgeGatewayTablesAsync(CancellationToken cancellationToken)
    {
        const string settingsSql = """
            CREATE TABLE IF NOT EXISTS edge_gateway_settings (
                Id INTEGER NOT NULL PRIMARY KEY,
                GatewaySubdomain TEXT NOT NULL,
                TunnelInstanceId TEXT NOT NULL DEFAULT '',
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(settingsSql, cancellationToken);
        await EnsureColumnExistsAsync("edge_gateway_settings", "TunnelInstanceId", "TEXT NOT NULL DEFAULT ''", cancellationToken);

        const string routesSql = """
            CREATE TABLE IF NOT EXISTS edge_gateway_routes (
                Id TEXT NOT NULL PRIMARY KEY,
                Enabled INTEGER NOT NULL,
                DisplayName TEXT NOT NULL,
                Hostname TEXT NOT NULL,
                DomainName TEXT NOT NULL,
                TargetScheme INTEGER NOT NULL,
                TargetHost TEXT NOT NULL,
                TargetPort INTEGER NOT NULL,
                TargetPathPrefix TEXT NOT NULL,
                AuthMode INTEGER NOT NULL,
                AllowedUsers TEXT NOT NULL,
                AllowedGroups TEXT NOT NULL,
                AllowLanOnly INTEGER NOT NULL,
                AllowKnownIps TEXT NOT NULL,
                Notes TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                LastTestStatus INTEGER NOT NULL,
                LastTestMessage TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(routesSql, cancellationToken);

        const string auditSql = """
            CREATE TABLE IF NOT EXISTS edge_gateway_audit_entries (
                Id TEXT NOT NULL PRIMARY KEY,
                TimestampUtc TEXT NOT NULL,
                Hostname TEXT NOT NULL,
                RouteId TEXT NULL,
                RequestedPath TEXT NOT NULL,
                SourceIp TEXT NOT NULL,
                UserEmail TEXT NOT NULL,
                Decision INTEGER NOT NULL,
                Reason TEXT NOT NULL,
                AuthMode INTEGER NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(auditSql, cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "DROP INDEX IF EXISTS IX_edge_gateway_routes_Hostname;",
            cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_edge_gateway_routes_Hostname ON edge_gateway_routes (Hostname);",
            cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_edge_gateway_routes_Hostname_TargetPathPrefix ON edge_gateway_routes (Hostname, TargetPathPrefix);",
            cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_edge_gateway_routes_DomainName ON edge_gateway_routes (DomainName);",
            cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_edge_gateway_routes_Enabled ON edge_gateway_routes (Enabled);",
            cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_edge_gateway_audit_entries_TimestampUtc ON edge_gateway_audit_entries (TimestampUtc);",
            cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_edge_gateway_audit_entries_Hostname ON edge_gateway_audit_entries (Hostname);",
            cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_edge_gateway_audit_entries_UserEmail ON edge_gateway_audit_entries (UserEmail);",
            cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_edge_gateway_audit_entries_Decision ON edge_gateway_audit_entries (Decision);",
            cancellationToken);
    }

    private async Task EnsureMessagingTablesAsync(CancellationToken cancellationToken)
    {
        const string emailSettingsSql = """
            CREATE TABLE IF NOT EXISTS messaging_email_settings (
                Id INTEGER NOT NULL PRIMARY KEY,
                IsEnabled INTEGER NOT NULL,
                Provider INTEGER NOT NULL,
                SenderAddress TEXT NOT NULL,
                SenderDisplayName TEXT NOT NULL,
                SmtpHost TEXT NOT NULL,
                SmtpPort INTEGER NOT NULL,
                SmtpUseStartTls INTEGER NOT NULL,
                SmtpUsername TEXT NULL,
                SmtpPasswordSecretReference TEXT NULL,
                GraphTenantId TEXT NOT NULL,
                GraphClientId TEXT NOT NULL,
                GraphClientSecretReference TEXT NULL,
                GraphAuthority TEXT NOT NULL,
                GraphBaseUrl TEXT NOT NULL,
                GraphSaveToSentItems INTEGER NOT NULL,
                LastVerifiedAtUtc TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(emailSettingsSql, cancellationToken);
        await EnsureColumnExistsAsync("messaging_email_settings", "LastVerifiedAtUtc", "TEXT NULL", cancellationToken);
    }

    private async Task EnsureMediaLibraryTablesAsync(CancellationToken cancellationToken)
    {
        const string settingsSql = """
            CREATE TABLE IF NOT EXISTS media_library_settings (
                Id INTEGER NOT NULL PRIMARY KEY,
                IsEnabled INTEGER NOT NULL,
                StreamingMode INTEGER NOT NULL,
                FfmpegPath TEXT NOT NULL,
                FfprobePath TEXT NOT NULL,
                EnableMetadataProbing INTEGER NOT NULL,
                EnableRemux INTEGER NOT NULL,
                EnableTranscoding INTEGER NOT NULL,
                MaxConcurrentFfmpegJobs INTEGER NOT NULL,
                TempCacheFolder TEXT NOT NULL,
                CacheExpiryMinutes INTEGER NOT NULL,
                MaxCacheSizeBytes INTEGER NOT NULL,
                HardwareAcceleration INTEGER NOT NULL,
                RequireLoginForPlaylists INTEGER NOT NULL,
                RequireLoginForStreams INTEGER NOT NULL,
                AllowLanAnonymousAccess INTEGER NOT NULL,
                GenerateTemporarySignedStreamUrls INTEGER NOT NULL,
                SignedUrlExpiryMinutes INTEGER NOT NULL,
                IpAllowlistCsv TEXT NOT NULL,
                ShowUnsupportedFiles INTEGER NOT NULL,
                AllowUnknownBrowserPlayback INTEGER NOT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(settingsSql, cancellationToken);

        const string rootsSql = """
            CREATE TABLE IF NOT EXISTS media_library_roots (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                Path TEXT NOT NULL,
                Category INTEGER NOT NULL,
                CustomCategoryName TEXT NOT NULL,
                Enabled INTEGER NOT NULL,
                Recursive INTEGER NOT NULL,
                IncludeExtensionsJson TEXT NOT NULL,
                ExcludeExtensionsJson TEXT NOT NULL,
                ExcludeFoldersJson TEXT NOT NULL,
                SortOrder INTEGER NOT NULL,
                Notes TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                LastScanUtc TEXT NULL,
                LastScanStatus INTEGER NOT NULL,
                LastScanMessage TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(rootsSql, cancellationToken);

        const string itemsSql = """
            CREATE TABLE IF NOT EXISTS media_items (
                Id TEXT NOT NULL PRIMARY KEY,
                LibraryRootId TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                FullPath TEXT NOT NULL,
                FileName TEXT NOT NULL,
                Extension TEXT NOT NULL,
                MediaKind INTEGER NOT NULL,
                SizeBytes INTEGER NOT NULL,
                LastModifiedUtc TEXT NULL,
                MimeType TEXT NOT NULL,
                DurationSeconds REAL NULL,
                VideoCodec TEXT NULL,
                AudioCodec TEXT NULL,
                Container TEXT NULL,
                IsBrowserCompatible INTEGER NULL,
                IsVlcCompatible INTEGER NULL,
                RequiresRemux INTEGER NULL,
                RequiresTranscode INTEGER NULL,
                IndexedUtc TEXT NOT NULL,
                FOREIGN KEY (LibraryRootId) REFERENCES media_library_roots(Id) ON DELETE CASCADE
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(itemsSql, cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_media_library_roots_Path ON media_library_roots (Path);",
            cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_media_library_roots_Enabled_SortOrder ON media_library_roots (Enabled, SortOrder);",
            cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_media_items_FullPath ON media_items (FullPath);",
            cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_media_items_LibraryRootId_RelativePath ON media_items (LibraryRootId, RelativePath);",
            cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_media_items_MediaKind_Extension ON media_items (MediaKind, Extension);",
            cancellationToken);
    }

    private async Task EnsureSftpTablesAsync(CancellationToken cancellationToken)
    {
        const string sftpHostSettingsSql = """
            CREATE TABLE IF NOT EXISTS sftp_host_settings (
                Id INTEGER NOT NULL PRIMARY KEY,
                IsManagedModeEnabled INTEGER NOT NULL,
                BasePath TEXT NOT NULL,
                DefaultAuthenticationMode INTEGER NOT NULL,
                PreferDropInConfiguration INTEGER NOT NULL,
                ManagedConfigPath TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                LastAppliedAtUtc TEXT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(sftpHostSettingsSql, cancellationToken);

        const string sftpManagedUsersSql = """
            CREATE TABLE IF NOT EXISTS sftp_managed_users (
                UserName TEXT NOT NULL PRIMARY KEY,
                AuthenticationMode INTEGER NOT NULL,
                IsEnabled INTEGER NOT NULL,
                HasPassword INTEGER NOT NULL,
                LoginShell TEXT NOT NULL,
                PrimaryGroup TEXT NOT NULL,
                SupplementaryGroupsJson TEXT NOT NULL,
                BasePath TEXT NOT NULL,
                ChrootPath TEXT NOT NULL,
                WritablePath TEXT NOT NULL,
                ChrootOwner TEXT NOT NULL,
                ChrootGroup TEXT NOT NULL,
                ChrootMode TEXT NOT NULL,
                WritableOwner TEXT NOT NULL,
                WritableGroup TEXT NOT NULL,
                WritableMode TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                PasswordChangedAtUtc TEXT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(sftpManagedUsersSql, cancellationToken);

        const string sftpPublicKeysSql = """
            CREATE TABLE IF NOT EXISTS sftp_public_keys (
                Id TEXT NOT NULL PRIMARY KEY,
                UserName TEXT NOT NULL,
                Label TEXT NOT NULL,
                KeyType TEXT NOT NULL,
                Fingerprint TEXT NOT NULL,
                PublicKeyText TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (UserName) REFERENCES sftp_managed_users(UserName) ON DELETE CASCADE
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(sftpPublicKeysSql, cancellationToken);

        const string sftpAuditEntriesSql = """
            CREATE TABLE IF NOT EXISTS sftp_audit_entries (
                Id TEXT NOT NULL PRIMARY KEY,
                EventType TEXT NOT NULL,
                TargetType TEXT NOT NULL,
                TargetKey TEXT NOT NULL,
                Summary TEXT NOT NULL,
                Details TEXT NOT NULL,
                Success INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                BackupSnapshotId TEXT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(sftpAuditEntriesSql, cancellationToken);

        const string sftpBackupSnapshotsSql = """
            CREATE TABLE IF NOT EXISTS sftp_backup_snapshots (
                Id TEXT NOT NULL PRIMARY KEY,
                Summary TEXT NOT NULL,
                FilesJson TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                RollbackAvailable INTEGER NOT NULL,
                StorageDirectory TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(sftpBackupSnapshotsSql, cancellationToken);
    }

    private async Task EnsurePortalConnectionSettingsCompatibilityAsync(CancellationToken cancellationToken)
    {
        await EnsurePortalConnectionSettingsColumnAsync(
            "InstanceIdentityPrivateKeySecretReference",
            "ALTER TABLE portal_connection_settings ADD COLUMN InstanceIdentityPrivateKeySecretReference TEXT NULL;",
            cancellationToken);
        await EnsurePortalConnectionSettingsColumnAsync(
            "InstanceIdentityPublicKey",
            "ALTER TABLE portal_connection_settings ADD COLUMN InstanceIdentityPublicKey TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsurePortalConnectionSettingsColumnAsync(
            "InstanceIdentityPublicKeyFingerprint",
            "ALTER TABLE portal_connection_settings ADD COLUMN InstanceIdentityPublicKeyFingerprint TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsurePortalConnectionSettingsColumnAsync(
            "PortalOrganizationName",
            "ALTER TABLE portal_connection_settings ADD COLUMN PortalOrganizationName TEXT NULL;",
            cancellationToken);
    }

    private async Task EnsurePortalConnectionSettingsColumnAsync(
        string columnName,
        string alterSql,
        CancellationToken cancellationToken)
    {
        await using var connection = dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('portal_connection_settings');";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await dbContext.Database.ExecuteSqlRawAsync(alterSql, cancellationToken);
    }

    private static ManagedHostEntity MapLocalManagedHost(LinuxMadeSane.Core.Models.ManagedHost host) =>
        new()
        {
            Id = host.Id,
            Name = host.Name,
            Hostname = host.Hostname,
            Port = host.Port,
            Environment = host.Environment,
            Description = host.Description,
            DefaultWorkingDirectory = host.DefaultWorkingDirectory,
            OperatingStatus = (int)host.OperatingStatus,
            PrimaryAuthenticationType = (int)host.PrimaryAuthenticationType,
            FallbackAuthenticationType = host.FallbackAuthenticationType.HasValue
                ? (int)host.FallbackAuthenticationType.Value
                : null,
            Username = host.Username,
            PasswordSecretReference = host.PasswordSecretReference,
            PrivateKeySecretReference = host.PrivateKeySecretReference,
            PrivateKeyPassphraseSecretReference = host.PrivateKeyPassphraseSecretReference,
            UseKeyboardInteractiveFallback = host.UseKeyboardInteractiveFallback,
            LastSeenUtc = host.LastSeenUtc,
            LastConnectionTestStatus = (int)host.LastConnectionTestStatus,
            Platform = host.Platform,
            Kind = (int)host.Kind
        };

    private async Task EnsureManagedHostColumnsAsync(CancellationToken cancellationToken)
    {
        await EnsureColumnExistsAsync("managed_hosts", "Kind", "INTEGER NOT NULL DEFAULT 0", cancellationToken);

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE managed_hosts
            SET Kind = {(int)ManagedHostKind.LmsHost}
            WHERE Id = {AiLocalMachine.ManagedHostId}
               OR lower(trim(Hostname)) = 'localhost'
               OR lower(trim(Hostname)) LIKE 'localhost:%'
               OR lower(trim(Hostname)) = '127.0.0.1'
               OR lower(trim(Hostname)) LIKE '127.%'
               OR lower(trim(Hostname)) = '::1'
               OR lower(trim(Hostname)) = '[::1]'
               OR lower(trim(Hostname)) LIKE 'http://localhost%'
               OR lower(trim(Hostname)) LIKE 'https://localhost%'
               OR lower(Name) LIKE '%linux made sane%'
               OR lower(Description) LIKE '%linux made sane%'
               OR lower(Platform) LIKE '%linux made sane%';
            """,
            cancellationToken);
    }

    private async Task EnsureAiTablesAsync(CancellationToken cancellationToken)
    {
        const string chatThreadsSql = """
            CREATE TABLE IF NOT EXISTS ai_chat_threads (
                Id TEXT NOT NULL PRIMARY KEY,
                Title TEXT NOT NULL,
                ProviderKey TEXT NOT NULL,
                ProviderType INTEGER NOT NULL,
                ModelId TEXT NOT NULL,
                ProviderConversationReference TEXT NOT NULL DEFAULT '',
                ProviderStateReference TEXT NOT NULL DEFAULT '',
                TrustLevel INTEGER NOT NULL,
                AllowReadOnlyTools INTEGER NOT NULL,
                AllowMutatingTools INTEGER NOT NULL,
                RequireApprovalForMediumRisk INTEGER NOT NULL,
                RequireApprovalForHighRisk INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(chatThreadsSql, cancellationToken);

        const string chatMessagesSql = """
            CREATE TABLE IF NOT EXISTS ai_chat_messages (
                Id TEXT NOT NULL PRIMARY KEY,
                ThreadId TEXT NOT NULL,
                SequenceNumber INTEGER NOT NULL,
                Role INTEGER NOT NULL,
                Content TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ThreadId) REFERENCES ai_chat_threads(Id) ON DELETE CASCADE
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(chatMessagesSql, cancellationToken);

        const string attachedServersSql = """
            CREATE TABLE IF NOT EXISTS ai_attached_servers (
                Id TEXT NOT NULL PRIMARY KEY,
                ThreadId TEXT NOT NULL,
                ManagedHostId TEXT NOT NULL,
                ServerName TEXT NOT NULL,
                Hostname TEXT NOT NULL,
                Environment TEXT NOT NULL,
                AttachedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ThreadId) REFERENCES ai_chat_threads(Id) ON DELETE CASCADE,
                FOREIGN KEY (ManagedHostId) REFERENCES managed_hosts(Id) ON DELETE RESTRICT
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(attachedServersSql, cancellationToken);

        const string executionPlansSql = """
            CREATE TABLE IF NOT EXISTS ai_execution_plans (
                Id TEXT NOT NULL PRIMARY KEY,
                ThreadId TEXT NOT NULL,
                MessageId TEXT NULL,
                Summary TEXT NOT NULL,
                Outcome INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ThreadId) REFERENCES ai_chat_threads(Id) ON DELETE CASCADE
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(executionPlansSql, cancellationToken);

        const string proposedActionsSql = """
            CREATE TABLE IF NOT EXISTS ai_proposed_actions (
                Id TEXT NOT NULL PRIMARY KEY,
                ExecutionPlanId TEXT NOT NULL,
                SequenceNumber INTEGER NOT NULL,
                Title TEXT NOT NULL,
                Description TEXT NOT NULL,
                ToolName TEXT NOT NULL,
                ToolArgumentsJson TEXT NOT NULL,
                CommandPreview TEXT NOT NULL DEFAULT '',
                RiskLevel INTEGER NOT NULL,
                ApprovalRequirement INTEGER NOT NULL DEFAULT 0,
                RequiredTrustLevel INTEGER NOT NULL DEFAULT 1,
                PolicyReason TEXT NOT NULL DEFAULT '',
                Outcome INTEGER NOT NULL,
                SafeChangeJson TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (ExecutionPlanId) REFERENCES ai_execution_plans(Id) ON DELETE CASCADE
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(proposedActionsSql, cancellationToken);

        const string approvalRequestsSql = """
            CREATE TABLE IF NOT EXISTS ai_approval_requests (
                Id TEXT NOT NULL PRIMARY KEY,
                ThreadId TEXT NOT NULL,
                ExecutionPlanId TEXT NULL,
                ProposedActionId TEXT NULL,
                Title TEXT NOT NULL,
                Summary TEXT NOT NULL,
                ToolName TEXT NOT NULL DEFAULT '',
                CommandPreview TEXT NOT NULL DEFAULT '',
                RiskLevel INTEGER NOT NULL,
                Requirement INTEGER NOT NULL DEFAULT 0,
                RequiredTrustLevel INTEGER NOT NULL DEFAULT 1,
                State INTEGER NOT NULL,
                PolicyReason TEXT NOT NULL DEFAULT '',
                RememberDecisionSupported INTEGER NOT NULL DEFAULT 0,
                RequestedAtUtc TEXT NOT NULL,
                DecidedBy TEXT NULL,
                DecisionReason TEXT NULL,
                DecidedAtUtc TEXT NULL,
                FOREIGN KEY (ThreadId) REFERENCES ai_chat_threads(Id) ON DELETE CASCADE
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(approvalRequestsSql, cancellationToken);

        const string approvalDecisionsSql = """
            CREATE TABLE IF NOT EXISTS ai_approval_decisions (
                ApprovalRequestId TEXT NOT NULL PRIMARY KEY,
                State INTEGER NOT NULL,
                DecisionType INTEGER NOT NULL,
                DecidedBy TEXT NOT NULL,
                DecidedByTrustLevel INTEGER NOT NULL,
                AdminOverrideUsed INTEGER NOT NULL,
                RememberDecision INTEGER NOT NULL,
                Reason TEXT NOT NULL,
                DecidedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ApprovalRequestId) REFERENCES ai_approval_requests(Id) ON DELETE CASCADE
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(approvalDecisionsSql, cancellationToken);

        const string toolInvocationsSql = """
            CREATE TABLE IF NOT EXISTS ai_tool_invocations (
                Id TEXT NOT NULL PRIMARY KEY,
                ThreadId TEXT NOT NULL,
                MessageId TEXT NULL,
                ExecutionPlanId TEXT NULL,
                ProposedActionId TEXT NULL,
                ToolName TEXT NOT NULL,
                ArgumentsJson TEXT NOT NULL,
                Status INTEGER NOT NULL,
                StartedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL,
                FOREIGN KEY (ThreadId) REFERENCES ai_chat_threads(Id) ON DELETE CASCADE
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(toolInvocationsSql, cancellationToken);

        const string chatRunsSql = """
            CREATE TABLE IF NOT EXISTS ai_chat_runs (
                Id TEXT NOT NULL PRIMARY KEY,
                ThreadId TEXT NOT NULL,
                MessageId TEXT NOT NULL,
                Status INTEGER NOT NULL,
                Step INTEGER NOT NULL,
                StatusSummary TEXT NOT NULL,
                ExecutionPlanId TEXT NULL,
                ProviderAttemptCount INTEGER NOT NULL DEFAULT 0,
                CurrentProviderResponseId TEXT NOT NULL DEFAULT '',
                PendingAssistantOutputsJson TEXT NOT NULL DEFAULT '',
                PendingToolCallsJson TEXT NOT NULL DEFAULT '',
                LastError TEXT NOT NULL DEFAULT '',
                CancellationRequested INTEGER NOT NULL DEFAULT 0,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL,
                FOREIGN KEY (ThreadId) REFERENCES ai_chat_threads(Id) ON DELETE CASCADE,
                FOREIGN KEY (MessageId) REFERENCES ai_chat_messages(Id) ON DELETE CASCADE
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(chatRunsSql, cancellationToken);

        const string toolResultsSql = """
            CREATE TABLE IF NOT EXISTS ai_tool_results (
                Id TEXT NOT NULL PRIMARY KEY,
                InvocationId TEXT NOT NULL,
                Outcome INTEGER NOT NULL,
                Summary TEXT NOT NULL,
                OutputText TEXT NOT NULL,
                ErrorText TEXT NOT NULL,
                PayloadJson TEXT NOT NULL DEFAULT '',
                ExitCode INTEGER NULL,
                CompletedAtUtc TEXT NOT NULL,
                FOREIGN KEY (InvocationId) REFERENCES ai_tool_invocations(Id) ON DELETE CASCADE
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(toolResultsSql, cancellationToken);

        const string auditEntriesSql = """
            CREATE TABLE IF NOT EXISTS ai_audit_entries (
                Id TEXT NOT NULL PRIMARY KEY,
                ThreadId TEXT NOT NULL,
                MessageId TEXT NULL,
                EventType TEXT NOT NULL,
                Summary TEXT NOT NULL,
                Details TEXT NOT NULL,
                MetadataJson TEXT NOT NULL DEFAULT '',
                Outcome INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ThreadId) REFERENCES ai_chat_threads(Id) ON DELETE CASCADE
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(auditEntriesSql, cancellationToken);

        const string chatCheckpointsSql = """
            CREATE TABLE IF NOT EXISTS ai_chat_checkpoints (
                Id TEXT NOT NULL PRIMARY KEY,
                ThreadId TEXT NOT NULL,
                MessageId TEXT NULL,
                Label TEXT NOT NULL,
                Summary TEXT NOT NULL,
                StateJson TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ThreadId) REFERENCES ai_chat_threads(Id) ON DELETE CASCADE
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(chatCheckpointsSql, cancellationToken);

        const string threadUpdatedIndexSql = """
            CREATE INDEX IF NOT EXISTS IX_ai_chat_threads_UpdatedAtUtc
            ON ai_chat_threads (UpdatedAtUtc);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(threadUpdatedIndexSql, cancellationToken);

        const string messageThreadSequenceIndexSql = """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ai_chat_messages_ThreadId_SequenceNumber
            ON ai_chat_messages (ThreadId, SequenceNumber);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(messageThreadSequenceIndexSql, cancellationToken);

        const string attachedServerIndexSql = """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ai_attached_servers_ThreadId_ManagedHostId
            ON ai_attached_servers (ThreadId, ManagedHostId);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(attachedServerIndexSql, cancellationToken);

        const string executionPlansIndexSql = """
            CREATE INDEX IF NOT EXISTS IX_ai_execution_plans_ThreadId_CreatedAtUtc
            ON ai_execution_plans (ThreadId, CreatedAtUtc);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(executionPlansIndexSql, cancellationToken);

        const string proposedActionsIndexSql = """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ai_proposed_actions_ExecutionPlanId_SequenceNumber
            ON ai_proposed_actions (ExecutionPlanId, SequenceNumber);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(proposedActionsIndexSql, cancellationToken);

        const string approvalRequestsIndexSql = """
            CREATE INDEX IF NOT EXISTS IX_ai_approval_requests_ThreadId_State
            ON ai_approval_requests (ThreadId, State);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(approvalRequestsIndexSql, cancellationToken);

        const string toolInvocationsIndexSql = """
            CREATE INDEX IF NOT EXISTS IX_ai_tool_invocations_ThreadId_StartedAtUtc
            ON ai_tool_invocations (ThreadId, StartedAtUtc);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(toolInvocationsIndexSql, cancellationToken);

        const string toolInvocationsProposedActionIndexSql = """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ai_tool_invocations_ProposedActionId
            ON ai_tool_invocations (ProposedActionId)
            WHERE ProposedActionId IS NOT NULL;
            """;

        await dbContext.Database.ExecuteSqlRawAsync(toolInvocationsProposedActionIndexSql, cancellationToken);

        const string toolResultsIndexSql = """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ai_tool_results_InvocationId
            ON ai_tool_results (InvocationId);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(toolResultsIndexSql, cancellationToken);

        const string auditEntriesIndexSql = """
            CREATE INDEX IF NOT EXISTS IX_ai_audit_entries_ThreadId_CreatedAtUtc
            ON ai_audit_entries (ThreadId, CreatedAtUtc);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(auditEntriesIndexSql, cancellationToken);

        const string chatCheckpointsIndexSql = """
            CREATE INDEX IF NOT EXISTS IX_ai_chat_checkpoints_ThreadId_CreatedAtUtc
            ON ai_chat_checkpoints (ThreadId, CreatedAtUtc);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(chatCheckpointsIndexSql, cancellationToken);

        const string chatRunsThreadUpdatedIndexSql = """
            CREATE INDEX IF NOT EXISTS IX_ai_chat_runs_ThreadId_UpdatedAtUtc
            ON ai_chat_runs (ThreadId, UpdatedAtUtc);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(chatRunsThreadUpdatedIndexSql, cancellationToken);

        const string chatRunsThreadStatusIndexSql = """
            CREATE INDEX IF NOT EXISTS IX_ai_chat_runs_ThreadId_Status
            ON ai_chat_runs (ThreadId, Status);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(chatRunsThreadStatusIndexSql, cancellationToken);

        const string chatRunsExecutionPlanIndexSql = """
            CREATE INDEX IF NOT EXISTS IX_ai_chat_runs_ExecutionPlanId
            ON ai_chat_runs (ExecutionPlanId);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(chatRunsExecutionPlanIndexSql, cancellationToken);

        const string chatRunsMessageIndexSql = """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ai_chat_runs_MessageId
            ON ai_chat_runs (MessageId);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(chatRunsMessageIndexSql, cancellationToken);

        const string providerSettingsSql = """
            CREATE TABLE IF NOT EXISTS ai_provider_settings (
                ProviderKey TEXT NOT NULL PRIMARY KEY,
                ProviderType INTEGER NOT NULL,
                DisplayName TEXT NOT NULL,
                IsEnabled INTEGER NOT NULL,
                IsDefault INTEGER NOT NULL,
                BaseUrl TEXT NOT NULL,
                DefaultModelId TEXT NOT NULL,
                StreamingEnabled INTEGER NOT NULL,
                ToolUseEnabled INTEGER NOT NULL,
                Notes TEXT NOT NULL,
                MetadataJson TEXT NOT NULL,
                ApiKeySecretReference TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(providerSettingsSql, cancellationToken);

        const string providerSettingsDefaultIndexSql = """
            CREATE INDEX IF NOT EXISTS IX_ai_provider_settings_IsDefault
            ON ai_provider_settings (IsDefault);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(providerSettingsDefaultIndexSql, cancellationToken);

        const string providerSettingsTypeEnabledIndexSql = """
            CREATE INDEX IF NOT EXISTS IX_ai_provider_settings_ProviderType_IsEnabled
            ON ai_provider_settings (ProviderType, IsEnabled);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(providerSettingsTypeEnabledIndexSql, cancellationToken);

        const string protectedSecretsSql = """
            CREATE TABLE IF NOT EXISTS protected_secrets (
                Reference TEXT NOT NULL PRIMARY KEY,
                Purpose TEXT NOT NULL,
                ProtectedValue TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(protectedSecretsSql, cancellationToken);

        await EnsureColumnExistsAsync("ai_proposed_actions", "CommandPreview", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnExistsAsync("ai_proposed_actions", "ApprovalRequirement", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnExistsAsync("ai_proposed_actions", "RequiredTrustLevel", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureColumnExistsAsync("ai_proposed_actions", "PolicyReason", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnExistsAsync("ai_proposed_actions", "ProviderToolCallId", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnExistsAsync("ai_proposed_actions", "SafeChangeJson", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnExistsAsync("ai_chat_threads", "ProviderConversationReference", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnExistsAsync("ai_chat_threads", "ProviderStateReference", "TEXT NOT NULL DEFAULT ''", cancellationToken);

        await EnsureColumnExistsAsync("ai_approval_requests", "ToolName", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnExistsAsync("ai_approval_requests", "CommandPreview", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnExistsAsync("ai_approval_requests", "Requirement", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnExistsAsync("ai_approval_requests", "RequiredTrustLevel", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureColumnExistsAsync("ai_approval_requests", "PolicyReason", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnExistsAsync("ai_approval_requests", "RememberDecisionSupported", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnExistsAsync("ai_tool_invocations", "ProposedActionId", "TEXT NULL", cancellationToken);
        await EnsureColumnExistsAsync("ai_tool_results", "PayloadJson", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnExistsAsync("ai_audit_entries", "MetadataJson", "TEXT NOT NULL DEFAULT ''", cancellationToken);
    }

    private async Task EnsureLocalAiTablesAsync(CancellationToken cancellationToken)
    {
        const string settingsSql = """
            CREATE TABLE IF NOT EXISTS local_ai_engine_settings (
                Id INTEGER NOT NULL PRIMARY KEY,
                RuntimeKind INTEGER NOT NULL,
                RuntimeEndpoint TEXT NOT NULL,
                DefaultModelId TEXT NOT NULL,
                LocalProviderKey TEXT NOT NULL,
                SharingEnabled INTEGER NOT NULL,
                AllowOrganizationInstances INTEGER NOT NULL,
                AllowedOrganizationIdsJson TEXT NOT NULL,
                AllowedInstanceIdsJson TEXT NOT NULL,
                AllowedModelIdsJson TEXT NOT NULL,
                MaxConcurrentRequests INTEGER NOT NULL,
                MaxQueuedRequests INTEGER NOT NULL,
                MaxRequestsPerMinute INTEGER NOT NULL,
                MaxPromptCharacters INTEGER NOT NULL,
                RequestTimeoutSeconds INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                LastSharedAtUtc TEXT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(settingsSql, cancellationToken);

        const string installedModelsSql = """
            CREATE TABLE IF NOT EXISTS local_ai_installed_models (
                ModelId TEXT NOT NULL PRIMARY KEY,
                DisplayName TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL,
                Digest TEXT NOT NULL,
                ModifiedAtUtc TEXT NULL,
                IsRunning INTEGER NOT NULL,
                IsDefault INTEGER NOT NULL,
                Capabilities INTEGER NOT NULL,
                Detail TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(installedModelsSql, cancellationToken);

        const string hardwareSnapshotsSql = """
            CREATE TABLE IF NOT EXISTS local_ai_hardware_snapshots (
                Id TEXT NOT NULL PRIMARY KEY,
                CpuModel TEXT NOT NULL,
                PhysicalCoreCount INTEGER NOT NULL,
                LogicalCoreCount INTEGER NOT NULL,
                TotalMemoryBytes INTEGER NOT NULL,
                AvailableMemoryBytes INTEGER NOT NULL,
                AvailableDiskBytes INTEGER NOT NULL,
                GpuAccelerationState INTEGER NOT NULL,
                GpusJson TEXT NOT NULL,
                Summary TEXT NOT NULL,
                CapturedAtUtc TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(hardwareSnapshotsSql, cancellationToken);

        const string benchmarkSql = """
            CREATE TABLE IF NOT EXISTS local_ai_benchmark_results (
                Id TEXT NOT NULL PRIMARY KEY,
                ModelId TEXT NOT NULL,
                PromptSummary TEXT NOT NULL,
                Succeeded INTEGER NOT NULL,
                DurationMilliseconds INTEGER NOT NULL,
                Detail TEXT NOT NULL,
                ExecutedAtUtc TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(benchmarkSql, cancellationToken);

        const string usageSql = """
            CREATE TABLE IF NOT EXISTS local_ai_usage_entries (
                Id TEXT NOT NULL PRIMARY KEY,
                ProviderKey TEXT NOT NULL,
                Scope INTEGER NOT NULL,
                ConsumerOrganizationId TEXT NULL,
                ConsumerInstanceId TEXT NULL,
                ConsumerDisplayName TEXT NOT NULL,
                ModelId TEXT NOT NULL,
                Succeeded INTEGER NOT NULL,
                DurationMilliseconds INTEGER NOT NULL,
                PromptCharacterCount INTEGER NOT NULL,
                OutputCharacterCount INTEGER NOT NULL,
                UsedToolCalls INTEGER NOT NULL,
                ResultSummary TEXT NOT NULL,
                StartedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(usageSql, cancellationToken);

        const string auditSql = """
            CREATE TABLE IF NOT EXISTS local_ai_audit_entries (
                Id TEXT NOT NULL PRIMARY KEY,
                EventType TEXT NOT NULL,
                Scope TEXT NOT NULL,
                Summary TEXT NOT NULL,
                Detail TEXT NOT NULL,
                Succeeded INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(auditSql, cancellationToken);
    }

    private async Task EnsureSecurityTablesAsync(CancellationToken cancellationToken)
    {
        var securityUsersSql = $"""
            CREATE TABLE IF NOT EXISTS security_users (
                Id TEXT NOT NULL PRIMARY KEY,
                Email TEXT NOT NULL,
                NormalizedEmail TEXT NOT NULL,
                LinuxUsername TEXT NOT NULL DEFAULT '',
                IsEnabled INTEGER NOT NULL,
                SessionLifetimeMinutes INTEGER NOT NULL DEFAULT {SecuritySessionPolicy.DefaultSessionLifetimeMinutes},
                SshAuthenticationMode INTEGER NOT NULL DEFAULT 0,
                AuthorizedKeyEntries TEXT NOT NULL DEFAULT '',
                IsLocalAccountManaged INTEGER NOT NULL DEFAULT 0,
                OtpSecretReference TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                LastLoginAtUtc TEXT NULL,
                PasswordChangedAtUtc TEXT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(securityUsersSql, cancellationToken);

        const string securityUsersEmailIndexSql = """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_security_users_NormalizedEmail
            ON security_users (NormalizedEmail);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(securityUsersEmailIndexSql, cancellationToken);

        await EnsureColumnExistsAsync(
            "security_users",
            "LinuxUsername",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnExistsAsync(
            "security_users",
            "SshAuthenticationMode",
            "INTEGER NOT NULL DEFAULT 0",
            cancellationToken);
        await EnsureColumnExistsAsync(
            "security_users",
            "AuthorizedKeyEntries",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnExistsAsync(
            "security_users",
            "IsLocalAccountManaged",
            "INTEGER NOT NULL DEFAULT 0",
            cancellationToken);
        await EnsureColumnExistsAsync(
            "security_users",
            "SessionLifetimeMinutes",
            $"INTEGER NOT NULL DEFAULT {SecuritySessionPolicy.DefaultSessionLifetimeMinutes}",
            cancellationToken);
        await EnsureColumnExistsAsync(
            "security_users",
            "PasswordChangedAtUtc",
            "TEXT NULL",
            cancellationToken);

        const string securityPasskeysSql = """
            CREATE TABLE IF NOT EXISTS security_passkey_credentials (
                Id TEXT NOT NULL PRIMARY KEY,
                UserId TEXT NOT NULL,
                CredentialId TEXT NOT NULL,
                PublicKey TEXT NOT NULL,
                UserHandle TEXT NOT NULL,
                SignatureCounter INTEGER NOT NULL,
                FriendlyName TEXT NOT NULL,
                IsBackedUp INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                LastUsedAtUtc TEXT NULL,
                FOREIGN KEY (UserId) REFERENCES security_users (Id) ON DELETE CASCADE
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(securityPasskeysSql, cancellationToken);

        const string securityPasskeysCredentialIndexSql = """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_security_passkey_credentials_CredentialId
            ON security_passkey_credentials (CredentialId);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(securityPasskeysCredentialIndexSql, cancellationToken);

        const string securityPasskeysUserIndexSql = """
            CREATE INDEX IF NOT EXISTS IX_security_passkey_credentials_UserId
            ON security_passkey_credentials (UserId);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(securityPasskeysUserIndexSql, cancellationToken);

        const string localInstanceIdentitySql = """
            CREATE TABLE IF NOT EXISTS local_instance_identity (
                Id INTEGER NOT NULL PRIMARY KEY,
                InstanceId TEXT NOT NULL,
                DisplayName TEXT NOT NULL,
                PrivateKeySecretReference TEXT NOT NULL,
                PublicKey TEXT NOT NULL,
                PublicKeyFingerprint TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                RegisteredWithPublicSiteAtUtc TEXT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(localInstanceIdentitySql, cancellationToken);

        const string localUserAccessPoliciesSql = """
            CREATE TABLE IF NOT EXISTS local_user_access_policies (
                UserName TEXT NOT NULL PRIMARY KEY,
                IsManagedPolicy INTEGER NOT NULL DEFAULT 0,
                SshAuthenticationMode INTEGER NOT NULL DEFAULT 0,
                AuthorizedKeyEntries TEXT NOT NULL DEFAULT '',
                UpdatedAtUtc TEXT NOT NULL,
                PasswordChangedAtUtc TEXT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(localUserAccessPoliciesSql, cancellationToken);
        await EnsureColumnExistsAsync(
            "local_user_access_policies",
            "IsManagedPolicy",
            "INTEGER NOT NULL DEFAULT 0",
            cancellationToken);

        const string trustedNetworksSql = """
            CREATE TABLE IF NOT EXISTS trusted_network_entries (
                Id TEXT NOT NULL PRIMARY KEY,
                Label TEXT NOT NULL,
                AddressOrCidr TEXT NOT NULL,
                Description TEXT NOT NULL,
                IsEnabled INTEGER NOT NULL,
                IsTrustedAccessEnabled INTEGER NOT NULL DEFAULT 1,
                IsAuthenticationEnabled INTEGER NOT NULL DEFAULT 1,
                IsBuiltIn INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(trustedNetworksSql, cancellationToken);
        await EnsureColumnExistsAsync(
            "trusted_network_entries",
            "IsTrustedAccessEnabled",
            "INTEGER NOT NULL DEFAULT 1",
            cancellationToken);
        await EnsureColumnExistsAsync(
            "trusted_network_entries",
            "IsAuthenticationEnabled",
            "INTEGER NOT NULL DEFAULT 1",
            cancellationToken);

        const string trustedNetworksAddressIndexSql = """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_trusted_network_entries_AddressOrCidr
            ON trusted_network_entries (AddressOrCidr);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(trustedNetworksAddressIndexSql, cancellationToken);
    }

    private async Task EnsureUserDisplayPreferenceTablesAsync(CancellationToken cancellationToken)
    {
        const string userDisplayPreferencesSql = """
            CREATE TABLE IF NOT EXISTS user_display_preferences (
                UserId TEXT NOT NULL PRIMARY KEY,
                ThemePaletteId TEXT NOT NULL,
                ThemeMode TEXT NOT NULL,
                FontScalePercent INTEGER NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(userDisplayPreferencesSql, cancellationToken);
    }

    private async Task EnsureFileBrowserShortcutTablesAsync(CancellationToken cancellationToken)
    {
        const string fileBrowserShortcutsSql = """
            CREATE TABLE IF NOT EXISTS file_browser_shortcuts (
                Id TEXT NOT NULL PRIMARY KEY,
                UserId TEXT NOT NULL,
                ManagedHostId TEXT NOT NULL,
                Label TEXT NOT NULL,
                TargetPath TEXT NOT NULL,
                SortOrder INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ManagedHostId) REFERENCES managed_hosts(Id) ON DELETE CASCADE
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(fileBrowserShortcutsSql, cancellationToken);

        const string fileBrowserShortcutsSortIndexSql = """
            CREATE INDEX IF NOT EXISTS IX_file_browser_shortcuts_UserId_ManagedHostId_SortOrder
            ON file_browser_shortcuts (UserId, ManagedHostId, SortOrder);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(fileBrowserShortcutsSortIndexSql, cancellationToken);

        const string fileBrowserShortcutsTargetIndexSql = """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_file_browser_shortcuts_UserId_ManagedHostId_TargetPath
            ON file_browser_shortcuts (UserId, ManagedHostId, TargetPath);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(fileBrowserShortcutsTargetIndexSql, cancellationToken);
    }

    private async Task EnsureCloudflareTablesAsync(CancellationToken cancellationToken)
    {
        const string cloudflareSettingsSql = """
            CREATE TABLE IF NOT EXISTS cloudflare_settings (
                ManagedHostId TEXT NOT NULL PRIMARY KEY,
                AccountId TEXT NOT NULL,
                AccountName TEXT NOT NULL,
                ZoneId TEXT NOT NULL,
                ZoneName TEXT NOT NULL,
                ApiTokenSecretReference TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ManagedHostId) REFERENCES managed_hosts(Id) ON DELETE CASCADE
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(cloudflareSettingsSql, cancellationToken);

        const string exposedServiceConfigsSql = """
            CREATE TABLE IF NOT EXISTS exposed_service_configs (
                Id TEXT NOT NULL PRIMARY KEY,
                ManagedHostId TEXT NOT NULL,
                ServiceName TEXT NOT NULL,
                AccountId TEXT NOT NULL,
                AccountName TEXT NOT NULL,
                ZoneId TEXT NOT NULL,
                ZoneName TEXT NOT NULL,
                Hostname TEXT NOT NULL,
                LocalServiceUrl TEXT NOT NULL,
                TunnelId TEXT NOT NULL,
                TunnelName TEXT NOT NULL,
                DnsRecordId TEXT NOT NULL,
                AccessApplicationId TEXT NULL,
                AccessPolicyId TEXT NULL,
                AccessMode INTEGER NOT NULL,
                AllowedEmailsJson TEXT NOT NULL DEFAULT '[]',
                AllowedEmailDomainsJson TEXT NOT NULL DEFAULT '[]',
                OriginRequestSettingsJson TEXT NOT NULL DEFAULT '{{}}',
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                DisabledAtUtc TEXT NULL,
                FOREIGN KEY (ManagedHostId) REFERENCES managed_hosts(Id) ON DELETE CASCADE
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(exposedServiceConfigsSql, cancellationToken);
        await EnsureColumnExistsAsync("exposed_service_configs", "OriginRequestSettingsJson", "TEXT NOT NULL DEFAULT '{{}}'", cancellationToken);

        const string exposedServiceHostHostnameIndexSql = """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_exposed_service_configs_ManagedHostId_Hostname
            ON exposed_service_configs (ManagedHostId, Hostname);
            """;

        await dbContext.Database.ExecuteSqlRawAsync(exposedServiceHostHostnameIndexSql, cancellationToken);
    }

    private async Task EnsureBuiltInTrustedNetworksAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var existingEntries = await dbContext.TrustedNetworkEntries
            .ToDictionaryAsync(entry => entry.AddressOrCidr, StringComparer.OrdinalIgnoreCase, cancellationToken);

        foreach (var definition in BuiltInTrustedNetworks)
        {
            if (!existingEntries.TryGetValue(definition.AddressOrCidr, out var entity))
            {
                dbContext.TrustedNetworkEntries.Add(new TrustedNetworkEntryEntity
                {
                    Id = definition.Id,
                    Label = definition.Label,
                    AddressOrCidr = definition.AddressOrCidr,
                    Description = definition.Description,
                    IsEnabled = true,
                    IsTrustedAccessEnabled = true,
                    IsAuthenticationEnabled = true,
                    IsBuiltIn = true,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                continue;
            }

            entity.Label = definition.Label;
            entity.Description = definition.Description;
            entity.IsBuiltIn = true;
            entity.UpdatedAtUtc = now;
        }
    }

    private static readonly IReadOnlyList<BuiltInTrustedNetworkDefinition> BuiltInTrustedNetworks =
    [
        new(Guid.Parse("2dbba9b9-b8a1-4fa8-bcb0-3b625937f8d3"), "Loopback IPv4", "127.0.0.0/8", "Always trust loopback IPv4 requests."),
        new(Guid.Parse("e488f64b-9af5-497d-b5d5-5f216c6e2fad"), "Loopback IPv6", "::1/128", "Always trust loopback IPv6 requests."),
        new(Guid.Parse("26944d6f-89e7-4cfb-b64e-0ceba04f875c"), "Private RFC1918 /8", "10.0.0.0/8", "Common LAN and VPN IPv4 range."),
        new(Guid.Parse("ca70d50d-65d0-412c-b62f-04dcd2a76cf0"), "Private RFC1918 /12", "172.16.0.0/12", "Common LAN and VPN IPv4 range."),
        new(Guid.Parse("59f448e5-e0e4-495e-855c-4cc0f368aa2c"), "Private RFC1918 /16", "192.168.0.0/16", "Common LAN IPv4 range."),
        new(Guid.Parse("96c2fffe-f3ae-4277-bd88-a088d56ba790"), "Tailscale CGNAT", "100.64.0.0/10", "Tailscale and CGNAT IPv4 range."),
        new(Guid.Parse("a078122d-5263-4ded-8067-9a2f1816518d"), "Unique local IPv6", "fc00::/7", "Private IPv6 address space."),
        new(Guid.Parse("1bd57914-5ec6-4b8a-a9c2-c21b0a362dc2"), "Link-local IPv6", "fe80::/10", "Direct local IPv6 interfaces."),
        new(Guid.Parse("71839414-3bd4-4b4f-9c0a-4fb35f6337d9"), "Tailscale IPv6", "fd7a:115c:a1e0::/48", "Tailscale IPv6 network range.")
    ];

    private sealed record BuiltInTrustedNetworkDefinition(Guid Id, string Label, string AddressOrCidr, string Description);

    private sealed record LocalManagedHostBootstrap(
        bool Enabled,
        string Username,
        string PrivateKeyPath,
        int Port,
        string DefaultWorkingDirectory,
        bool ForceUpdate)
    {
        public static LocalManagedHostBootstrap Disabled { get; } = new(false, string.Empty, string.Empty, 22, "/home", false);
    }

    private void EnsureDatabaseDirectoryExists()
    {
        var dataSource = dbContext.Database.GetDbConnection().DataSource;
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            return;
        }

        var fullPath = Path.IsPathRooted(dataSource)
            ? dataSource
            : Path.GetFullPath(dataSource, AppContext.BaseDirectory);

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private async Task EnsureColumnExistsAsync(
        string tableName,
        string columnName,
        string sqlDefinition,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await reader.DisposeAsync();
        var sql = string.Concat("ALTER TABLE ", tableName, " ADD COLUMN ", columnName, " ", sqlDefinition, ";");
        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private async Task EnsureScheduledTaskColumnsAsync(CancellationToken cancellationToken)
    {
        await EnsureColumnExistsAsync("scheduled_tasks", "RunbookId", "TEXT NULL", cancellationToken);
        await EnsureColumnExistsAsync("scheduled_tasks", "ExecutionToken", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnExistsAsync("scheduled_tasks", "CopyDeleteSourceAfterCopy", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnExistsAsync("scheduled_tasks", "MatchPatternsCsv", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnExistsAsync("scheduled_tasks", "MatchCaseInsensitive", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnExistsAsync("scheduled_tasks", "AgeFilterMode", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnExistsAsync("scheduled_tasks", "AgeFilterValue", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnExistsAsync("scheduled_tasks", "AgeFilterUnit", "INTEGER NOT NULL DEFAULT 2", cancellationToken);
        await EnsureColumnExistsAsync("scheduled_tasks", "CleanupDeleteFiles", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureColumnExistsAsync("scheduled_tasks", "CleanupDeleteDirectories", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
    }
}
