using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalSftpServerConfigurationService(
    ISftpServerStore store,
    ISftpServerInspectionService inspectionService,
    ISshdConfigService sshdConfigService,
    ISftpBackupService backupService,
    ISftpAuditService auditService,
    IPackageManagementService packageManagementService,
    ILinuxCommandRunner commandRunner) : ISftpServerConfigurationService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private const string OpenSshServerPackageName = "openssh-server";

    public async Task<SftpConfigurationPlan> BuildHostPlanAsync(
        SftpHostSettings settings,
        IReadOnlyList<SftpManagedUser> users,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var normalizedSettings = NormalizeSettings(settings);
        var inspection = await inspectionService.InspectAsync(cancellationToken);
        var warnings = new List<string>(inspection.Warnings);

        if (!inspection.IsOpenSshInstalled)
        {
            warnings.Add("OpenSSH is not installed on this LMS host yet.");
        }

        if (!string.Equals(inspection.Settings.BasePath, normalizedSettings.BasePath, StringComparison.Ordinal) &&
            users.Count > 0)
        {
            warnings.Add("Changing the SFTP base path after users exist is intentionally blocked for now to avoid orphaning chroot folders.");
        }

        var useDropIn = normalizedSettings.PreferDropInConfiguration && inspection.SupportsDropInConfiguration;
        var proposedConfig = sshdConfigService.BuildManagedConfig(normalizedSettings.BasePath);
        var validation = inspection.IsOpenSshInstalled
            ? await sshdConfigService.ValidateConfigurationAsync(proposedConfig, useDropIn, cancellationToken)
            : new SftpValidationResult(
                true,
                "OpenSSH is not installed yet. Linux Made Sane will install openssh-server first, then validate sshd before reloading the SSH service.",
                [],
                [],
                $"apt-get install -y {OpenSshServerPackageName}");

        var actions = new List<SftpConfigurationAction>();
        if (!inspection.IsOpenSshInstalled)
        {
            actions.Add(new SftpConfigurationAction(
                1,
                "Install OpenSSH server",
                $"Install the Ubuntu package '{OpenSshServerPackageName}' so sshd and internal-sftp are available on this LMS host.",
                $"apt-get update && apt-get install -y {OpenSshServerPackageName}",
                true,
                null,
                null));
        }

        actions.Add(new SftpConfigurationAction(
            actions.Count + 1,
            "Create LMS SFTP groups",
            "Ensure the managed SFTP groups exist for base access and authentication policy mapping.",
            "groupadd --system lms-sftp-users ; groupadd --system lms-sftp-password ; groupadd --system lms-sftp-key ; groupadd --system lms-sftp-key-password",
            true,
            null,
            null));
        actions.Add(new SftpConfigurationAction(
            actions.Count + 1,
            "Prepare SFTP folders",
            $"Create the base SFTP path '{normalizedSettings.BasePath}' and the managed authorized_keys directory.",
            $"install -d -m 755 {normalizedSettings.BasePath} ; install -d -m 755 {SftpServerDefaults.ManagedRootDirectory} ; install -d -m 755 {SftpServerDefaults.ManagedAuthorizedKeysDirectory}",
            true,
            normalizedSettings.BasePath,
            null));

        var configActions = await sshdConfigService.BuildApplyActionsAsync(proposedConfig, useDropIn, cancellationToken);
        actions.AddRange(configActions.Select((item, index) => item with { Order = actions.Count + index + 1 }));

        var backupSummary = useDropIn
            ? $"Backup {SftpServerDefaults.ManagedDropInPath} before replacing the LMS SFTP drop-in."
            : $"Backup {SftpServerDefaults.MainSshdConfigPath} before updating the LMS-managed block.";

        return new SftpConfigurationPlan(
            Guid.NewGuid(),
            "Configure LMS-managed SFTP host",
            dryRun,
            warnings,
            actions,
            validation,
            proposedConfig,
            backupSummary);
    }

    public async Task<SftpApplyResult> ApplyHostPlanAsync(
        SftpHostSettings settings,
        IReadOnlyList<SftpManagedUser> users,
        bool approved,
        CancellationToken cancellationToken = default)
    {
        if (!approved)
        {
            throw new InvalidOperationException("Approving the plan is required before Linux Made Sane will mutate the SFTP host.");
        }

        var normalizedSettings = NormalizeSettings(settings);
        var plan = await BuildHostPlanAsync(normalizedSettings, users, dryRun: false, cancellationToken);
        if (plan.Validation.IsValid == false)
        {
            return new SftpApplyResult(
                false,
                plan.Validation.Summary,
                [],
                plan.Validation,
                null,
                false,
                plan.Warnings);
        }

        var inspection = await inspectionService.InspectAsync(cancellationToken);
        if (users.Count > 0 &&
            !string.Equals(inspection.Settings.BasePath, normalizedSettings.BasePath, StringComparison.Ordinal))
        {
            return new SftpApplyResult(
                false,
                "Changing the SFTP base path while users already exist is blocked.",
                [],
                plan.Validation,
                null,
                false,
                plan.Warnings);
        }
        var logs = new List<OperationLogEntry>();
        SftpBackupSnapshot? backupSnapshot = null;

        try
        {
            if (!inspection.IsOpenSshInstalled)
            {
                logs.AddRange(await InstallOpenSshServerAsync(cancellationToken));
                inspection = await inspectionService.InspectAsync(cancellationToken);
            }

            var useDropIn = normalizedSettings.PreferDropInConfiguration && inspection.SupportsDropInConfiguration;
            var configValidation = await sshdConfigService.ValidateConfigurationAsync(
                plan.ProposedSshdConfig ?? string.Empty,
                useDropIn,
                cancellationToken);
            if (!configValidation.IsValid)
            {
                await auditService.RecordAsync(
                    new SftpAuditEntry(
                        Guid.NewGuid(),
                        "host.apply",
                        "host",
                        "local",
                        configValidation.Summary,
                        "The proposed LMS-managed SFTP configuration did not validate cleanly.",
                        false,
                        DateTimeOffset.UtcNow,
                        null),
                    cancellationToken);

                return new SftpApplyResult(
                    false,
                    configValidation.Summary,
                    logs,
                    configValidation,
                    null,
                    false,
                    plan.Warnings);
            }

            var backupTargets = useDropIn
                ? new[] { SftpServerDefaults.ManagedDropInPath }
                : new[] { SftpServerDefaults.MainSshdConfigPath };
            backupSnapshot = await backupService.CreateSnapshotAsync(
                "Before applying LMS SFTP sshd changes",
                backupTargets,
                cancellationToken);

            foreach (var group in SftpServerDefaults.ManagedGroups)
            {
                logs.AddRange(await EnsureGroupExistsAsync(group, cancellationToken));
            }

            logs.AddRange(await EnsureDirectoryAsync(normalizedSettings.BasePath, "755", $"Ensure SFTP base directory {normalizedSettings.BasePath} exists", cancellationToken));
            logs.AddRange(await EnsureDirectoryAsync(SftpServerDefaults.ManagedRootDirectory, "755", "Ensure LMS SFTP SSH root directory exists", cancellationToken));
            logs.AddRange(await EnsureDirectoryAsync(SftpServerDefaults.ManagedAuthorizedKeysDirectory, "755", "Ensure LMS SFTP authorized_keys directory exists", cancellationToken));

            var configResult = await sshdConfigService.ApplyManagedConfigurationAsync(
                plan.ProposedSshdConfig ?? string.Empty,
                useDropIn,
                backupSnapshot,
                cancellationToken);
            logs.AddRange(configResult.Logs);

            if (!configResult.Success)
            {
                await auditService.RecordAsync(
                    new SftpAuditEntry(
                        Guid.NewGuid(),
                        "host.apply",
                        "host",
                        "local",
                        configResult.Summary,
                        string.Join(Environment.NewLine, configResult.Warnings),
                        false,
                        DateTimeOffset.UtcNow,
                        backupSnapshot.Id),
                    cancellationToken);

                return configResult with { Logs = logs };
            }

            var now = DateTimeOffset.UtcNow;
            await store.SaveSettingsAsync(normalizedSettings with
            {
                IsManagedModeEnabled = true,
                LastAppliedAtUtc = now,
                UpdatedAtUtc = now
            }, cancellationToken);

            await auditService.RecordAsync(
                new SftpAuditEntry(
                    Guid.NewGuid(),
                    "host.apply",
                    "host",
                    "local",
                    "Applied LMS-managed SFTP host configuration.",
                    $"Base path: {normalizedSettings.BasePath}{Environment.NewLine}Managed config: {(useDropIn ? SftpServerDefaults.ManagedDropInPath : SftpServerDefaults.MainSshdConfigPath)}",
                    true,
                    now,
                    backupSnapshot.Id),
                cancellationToken);

            return configResult with
            {
                Summary = "Configured this LMS host for managed SFTP access.",
                Logs = logs,
                BackupSnapshot = backupSnapshot,
                Warnings = plan.Warnings
            };
        }
        catch (Exception exception)
        {
            await auditService.RecordAsync(
                new SftpAuditEntry(
                    Guid.NewGuid(),
                    "host.apply",
                    "host",
                    "local",
                    exception.Message,
                    "The LMS SFTP host apply flow failed before completion.",
                    false,
                    DateTimeOffset.UtcNow,
                    backupSnapshot?.Id),
                cancellationToken);

            return new SftpApplyResult(
                false,
                exception.Message,
                logs,
                plan.Validation,
                backupSnapshot,
                false,
                plan.Warnings);
        }
    }

    private async Task<IReadOnlyList<OperationLogEntry>> InstallOpenSshServerAsync(CancellationToken cancellationToken)
    {
        var logs = await packageManagementService.ApplyActionsAsync(
            [
                new PackageAction(
                    PackageActionKind.Install,
                    OpenSshServerPackageName,
                    "Install the local OpenSSH server package before Linux Made Sane writes managed internal-sftp rules.",
                    false,
                    $"apt-get install -y {OpenSshServerPackageName}")
            ],
            dryRun: false,
            cancellationToken);

        var failure = logs.FirstOrDefault(log => log.ExitCode.HasValue && log.ExitCode.Value != 0);
        if (failure is not null)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not install '{OpenSshServerPackageName}': {failure.StandardError ?? failure.StandardOutput ?? $"exit code {failure.ExitCode}"}");
        }

        return logs;
    }

    private async Task<IReadOnlyList<OperationLogEntry>> EnsureGroupExistsAsync(string groupName, CancellationToken cancellationToken)
    {
        var getent = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "getent",
                ["group", groupName],
                false,
                CommandTimeout,
                $"Check Linux group {groupName}"),
            dryRun: false,
            cancellationToken);

        if (getent.ExitCode == 0)
        {
            return [SftpSystemCommandHelper.MapLog(getent, $"Linux group {groupName} already exists")];
        }

        var create = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "groupadd",
                ["--system", groupName],
                true,
                CommandTimeout,
                $"Create Linux group {groupName}"),
            dryRun: false,
            cancellationToken);

        if (create.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not create the SFTP group '{groupName}': {SftpSystemCommandHelper.BuildFailureDetail(create)}");
        }

        return [SftpSystemCommandHelper.MapLog(create, $"Create Linux group {groupName}")];
    }

    private async Task<IReadOnlyList<OperationLogEntry>> EnsureDirectoryAsync(
        string path,
        string mode,
        string description,
        CancellationToken cancellationToken)
    {
        var create = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "install",
                ["-d", "-m", mode, path],
                true,
                CommandTimeout,
                description),
            dryRun: false,
            cancellationToken);

        if (create.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not prepare '{path}' for SFTP management: {SftpSystemCommandHelper.BuildFailureDetail(create)}");
        }

        return [SftpSystemCommandHelper.MapLog(create, description)];
    }

    private static SftpHostSettings NormalizeSettings(SftpHostSettings settings)
    {
        var basePath = NormalizePath(settings.BasePath);
        var managedConfigPath = settings.PreferDropInConfiguration
            ? SftpServerDefaults.ManagedDropInPath
            : SftpServerDefaults.MainSshdConfigPath;
        var createdAtUtc = settings.CreatedAtUtc == default ? DateTimeOffset.UtcNow : settings.CreatedAtUtc;

        return settings with
        {
            BasePath = basePath,
            ManagedConfigPath = managedConfigPath,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static string NormalizePath(string path)
    {
        var normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return SftpServerDefaults.BasePath;
        }

        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = "/" + normalized.TrimStart('/');
        }

        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
    }
}
