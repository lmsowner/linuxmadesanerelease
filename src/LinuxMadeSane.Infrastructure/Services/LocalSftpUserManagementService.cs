// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Diagnostics;
using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalSftpUserManagementService(
    ISftpServerStore store,
    ISftpAuthPolicyService authPolicyService,
    ISftpChrootService chrootService,
    ISftpAuditService auditService,
    ILinuxCommandRunner commandRunner) : ISftpUserManagementService
{
    private static readonly Regex UserNameRegex = new("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.Compiled);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public async Task<SftpConfigurationPlan> BuildCreateOrUpdatePlanAsync(
        SftpManagedUser user,
        string? newPassword,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var settings = await store.GetSettingsAsync(cancellationToken);
        var existing = await store.GetUserAsync(user.UserName, cancellationToken);
        var normalizedUser = PrepareUser(user, settings.BasePath, existing, newPassword);
        var actions = BuildCreateOrUpdateActions(normalizedUser, existing is null, newPassword);

        return new SftpConfigurationPlan(
            Guid.NewGuid(),
            existing is null ? $"Create SFTP user {normalizedUser.UserName}" : $"Update SFTP user {normalizedUser.UserName}",
            dryRun,
            [],
            actions,
            new SftpValidationResult(true, "The requested SFTP user change is structurally valid.", [], [], null),
            null,
            null);
    }

    public async Task<SftpApplyResult> ApplyCreateOrUpdateAsync(
        SftpManagedUser user,
        string? newPassword,
        bool approved,
        CancellationToken cancellationToken = default)
    {
        if (!approved)
        {
            throw new InvalidOperationException("Approving the SFTP user plan is required before Linux Made Sane will apply it.");
        }

        var settings = await store.GetSettingsAsync(cancellationToken);
        var existing = await store.GetUserAsync(user.UserName, cancellationToken);
        var normalizedUser = PrepareUser(user, settings.BasePath, existing, newPassword);
        var logs = new List<OperationLogEntry>();

        try
        {
            await EnsureGroupExistsAsync(SftpServerDefaults.ManagedUsersGroup, logs, cancellationToken);
            foreach (var group in authPolicyService.GetSupplementaryGroups(normalizedUser.AuthenticationMode))
            {
                await EnsureGroupExistsAsync(group, logs, cancellationToken);
            }

            await EnsureDirectoryAsync(settings.BasePath, "755", $"Ensure base SFTP path {settings.BasePath}", logs, cancellationToken);
            await EnsureDirectoryAsync(SftpServerDefaults.ManagedAuthorizedKeysDirectory, "755", "Ensure SFTP authorized_keys directory", logs, cancellationToken);

            var systemUserExists = await SystemUserExistsAsync(normalizedUser.UserName, cancellationToken);
            if (systemUserExists && existing is null)
            {
                throw new InvalidOperationException($"Linux account '{normalizedUser.UserName}' already exists but is not tracked as an LMS-managed SFTP user.");
            }

            if (!systemUserExists)
            {
                await CreateSystemUserAsync(normalizedUser, logs, cancellationToken);
            }
            else
            {
                await UpdateSystemUserAsync(normalizedUser, logs, cancellationToken);
            }

            await SetAccountExpiryAsync(normalizedUser.UserName, normalizedUser.IsEnabled, logs, cancellationToken);
            await EnsureChrootFoldersAsync(normalizedUser, logs, cancellationToken);
            await ApplyAuthorizedKeysAsync(normalizedUser, logs, cancellationToken);
            await ApplyPasswordStateAsync(normalizedUser, existing, newPassword, logs, cancellationToken);

            await store.SaveUserAsync(normalizedUser, cancellationToken);
            await auditService.RecordAsync(
                new SftpAuditEntry(
                    Guid.NewGuid(),
                    existing is null ? "user.create" : "user.update",
                    "user",
                    normalizedUser.UserName,
                    existing is null
                        ? $"Created LMS-managed SFTP user '{normalizedUser.UserName}'."
                        : $"Updated LMS-managed SFTP user '{normalizedUser.UserName}'.",
                    $"Mode: {normalizedUser.AuthenticationMode}{Environment.NewLine}Enabled: {normalizedUser.IsEnabled}{Environment.NewLine}Writable path: {normalizedUser.Folder.WritablePath}",
                    true,
                    DateTimeOffset.UtcNow,
                    null),
                cancellationToken);

            return new SftpApplyResult(
                true,
                existing is null
                    ? $"Created SFTP user '{normalizedUser.UserName}'."
                    : $"Updated SFTP user '{normalizedUser.UserName}'.",
                logs,
                new SftpValidationResult(true, "The requested SFTP user change is structurally valid.", [], [], null),
                null,
                false,
                []);
        }
        catch (Exception exception)
        {
            await auditService.RecordAsync(
                new SftpAuditEntry(
                    Guid.NewGuid(),
                    existing is null ? "user.create" : "user.update",
                    "user",
                    normalizedUser.UserName,
                    exception.Message,
                    "The LMS SFTP user apply flow failed before completion.",
                    false,
                    DateTimeOffset.UtcNow,
                    null),
                cancellationToken);

            return new SftpApplyResult(
                false,
                exception.Message,
                logs,
                new SftpValidationResult(false, exception.Message, [exception.Message], [], null),
                null,
                false,
                []);
        }
    }

    public async Task<SftpConfigurationPlan> BuildDeletePlanAsync(
        string userName,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var user = await RequireExistingUserAsync(userName, cancellationToken);
        return new SftpConfigurationPlan(
            Guid.NewGuid(),
            $"Delete SFTP user {user.UserName}",
            dryRun,
            [],
            [
                new(1, "Delete Linux account", $"Delete Linux account '{user.UserName}'.", $"userdel -r {user.UserName} || userdel {user.UserName}", true, null, null),
                new(2, "Delete chroot folder", $"Remove the managed chroot folder '{user.Folder.ChrootPath}'.", $"rm -rf {user.Folder.ChrootPath}", true, user.Folder.ChrootPath, null),
                new(3, "Delete managed authorized_keys", $"Remove the managed authorized_keys file for '{user.UserName}'.", $"rm -f {SftpServerDefaults.ManagedAuthorizedKeysDirectory}/{user.UserName}", true, $"{SftpServerDefaults.ManagedAuthorizedKeysDirectory}/{user.UserName}", null)
            ],
            new SftpValidationResult(true, "The SFTP user delete plan is structurally valid.", [], [], null),
            null,
            null);
    }

    public async Task<SftpApplyResult> DeleteAsync(
        string userName,
        bool approved,
        CancellationToken cancellationToken = default)
    {
        if (!approved)
        {
            throw new InvalidOperationException("Approving the SFTP user delete plan is required before Linux Made Sane will apply it.");
        }

        var user = await RequireExistingUserAsync(userName, cancellationToken);
        var logs = new List<OperationLogEntry>();

        try
        {
            await RemoveAuthorizedKeysAsync(user.UserName, logs, cancellationToken);
            await DeleteSystemUserAsync(user.UserName, logs, cancellationToken);
            await DeleteFolderAsync(user, logs, cancellationToken);
            await store.DeleteUserAsync(user.UserName, cancellationToken);
            await auditService.RecordAsync(
                new SftpAuditEntry(
                    Guid.NewGuid(),
                    "user.delete",
                    "user",
                    user.UserName,
                    $"Deleted LMS-managed SFTP user '{user.UserName}'.",
                    $"Removed chroot path {user.Folder.ChrootPath}.",
                    true,
                    DateTimeOffset.UtcNow,
                    null),
                cancellationToken);

            return new SftpApplyResult(
                true,
                $"Deleted SFTP user '{user.UserName}'.",
                logs,
                new SftpValidationResult(true, "The SFTP user delete plan is structurally valid.", [], [], null),
                null,
                false,
                []);
        }
        catch (Exception exception)
        {
            await auditService.RecordAsync(
                new SftpAuditEntry(
                    Guid.NewGuid(),
                    "user.delete",
                    "user",
                    user.UserName,
                    exception.Message,
                    "The LMS SFTP user delete flow failed before completion.",
                    false,
                    DateTimeOffset.UtcNow,
                    null),
                cancellationToken);

            return new SftpApplyResult(
                false,
                exception.Message,
                logs,
                new SftpValidationResult(false, exception.Message, [exception.Message], [], null),
                null,
                false,
                []);
        }
    }

    public async Task<SftpConfigurationPlan> BuildDisablePlanAsync(
        string userName,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var user = await RequireExistingUserAsync(userName, cancellationToken);
        return new SftpConfigurationPlan(
            Guid.NewGuid(),
            $"Disable SFTP user {user.UserName}",
            dryRun,
            [],
            [
                new(1, "Expire the account", $"Set the Linux account expiry date for '{user.UserName}' so logins stop immediately.", $"usermod --expiredate 1 {user.UserName}", true, null, null)
            ],
            new SftpValidationResult(true, "The SFTP user disable plan is structurally valid.", [], [], null),
            null,
            null);
    }

    public async Task<SftpApplyResult> DisableAsync(
        string userName,
        bool approved,
        CancellationToken cancellationToken = default)
    {
        if (!approved)
        {
            throw new InvalidOperationException("Approving the SFTP user disable plan is required before Linux Made Sane will apply it.");
        }

        var existing = await RequireExistingUserAsync(userName, cancellationToken);
        var logs = new List<OperationLogEntry>();

        try
        {
            await SetAccountExpiryAsync(existing.UserName, enabled: false, logs, cancellationToken);
            await store.SaveUserAsync(existing with { IsEnabled = false, UpdatedAtUtc = DateTimeOffset.UtcNow }, cancellationToken);
            await auditService.RecordAsync(
                new SftpAuditEntry(
                    Guid.NewGuid(),
                    "user.disable",
                    "user",
                    existing.UserName,
                    $"Disabled LMS-managed SFTP user '{existing.UserName}'.",
                    "The Linux account expiry date was forced to stop new SFTP logins.",
                    true,
                    DateTimeOffset.UtcNow,
                    null),
                cancellationToken);

            return new SftpApplyResult(
                true,
                $"Disabled SFTP user '{existing.UserName}'.",
                logs,
                new SftpValidationResult(true, "The SFTP user disable plan is structurally valid.", [], [], null),
                null,
                false,
                []);
        }
        catch (Exception exception)
        {
            await auditService.RecordAsync(
                new SftpAuditEntry(
                    Guid.NewGuid(),
                    "user.disable",
                    "user",
                    existing.UserName,
                    exception.Message,
                    "The LMS SFTP user disable flow failed before completion.",
                    false,
                    DateTimeOffset.UtcNow,
                    null),
                cancellationToken);

            return new SftpApplyResult(
                false,
                exception.Message,
                logs,
                new SftpValidationResult(false, exception.Message, [exception.Message], [], null),
                null,
                false,
                []);
        }
    }

    public async Task<SftpConfigurationPlan> BuildPasswordResetPlanAsync(
        string userName,
        SftpAuthenticationMode authenticationMode,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireExistingUserAsync(userName, cancellationToken);
        if (authenticationMode == SftpAuthenticationMode.PublicKeyOnly)
        {
            throw new InvalidOperationException("Password reset is not available while the user is in PublicKeyOnly mode.");
        }

        return new SftpConfigurationPlan(
            Guid.NewGuid(),
            $"Reset password for {userName}",
            dryRun,
            [],
            [
                new(1, "Reset password", $"Apply a new Linux password for '{userName}' without storing the plaintext.", "chpasswd <redacted>", true, null, null)
            ],
            new SftpValidationResult(true, "The password reset plan is structurally valid.", [], [], null),
            null,
            null);
    }

    public async Task<SftpApplyResult> ResetPasswordAsync(
        string userName,
        SftpAuthenticationMode authenticationMode,
        string newPassword,
        bool approved,
        CancellationToken cancellationToken = default)
    {
        if (!approved)
        {
            throw new InvalidOperationException("Approving the SFTP password reset plan is required before Linux Made Sane will apply it.");
        }

        if (authenticationMode == SftpAuthenticationMode.PublicKeyOnly)
        {
            throw new InvalidOperationException("Password reset is not available while the user is in PublicKeyOnly mode.");
        }

        var user = await RequireExistingUserAsync(userName, cancellationToken);
        var logs = new List<OperationLogEntry>();

        try
        {
            await SetPasswordAsync(user.UserName, newPassword, logs, cancellationToken);
            await store.SaveUserAsync(user with
            {
                HasPassword = true,
                PasswordChangedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            }, cancellationToken);
            await auditService.RecordAsync(
                new SftpAuditEntry(
                    Guid.NewGuid(),
                    "user.password-reset",
                    "user",
                    user.UserName,
                    $"Reset the password for LMS-managed SFTP user '{user.UserName}'.",
                    "Linux Made Sane applied a new password without recording the plaintext value.",
                    true,
                    DateTimeOffset.UtcNow,
                    null),
                cancellationToken);

            return new SftpApplyResult(
                true,
                $"Reset the password for '{user.UserName}'.",
                logs,
                new SftpValidationResult(true, "The password reset plan is structurally valid.", [], [], null),
                null,
                false,
                []);
        }
        catch (Exception exception)
        {
            await auditService.RecordAsync(
                new SftpAuditEntry(
                    Guid.NewGuid(),
                    "user.password-reset",
                    "user",
                    user.UserName,
                    exception.Message,
                    "The LMS SFTP password reset flow failed before completion.",
                    false,
                    DateTimeOffset.UtcNow,
                    null),
                cancellationToken);

            return new SftpApplyResult(
                false,
                exception.Message,
                logs,
                new SftpValidationResult(false, exception.Message, [exception.Message], [], null),
                null,
                false,
                []);
        }
    }

    private SftpManagedUser PrepareUser(
        SftpManagedUser user,
        string basePath,
        SftpManagedUser? existing,
        string? newPassword)
    {
        var normalizedUserName = (user.UserName ?? string.Empty).Trim().ToLowerInvariant();
        if (!UserNameRegex.IsMatch(normalizedUserName))
        {
            throw new InvalidOperationException("Use a strict Linux username such as sftp-alice with only lowercase letters, digits, hyphens, or underscores.");
        }

        var publicKeys = user.PublicKeys
            .Where(static key => !string.IsNullOrWhiteSpace(key.PublicKeyText))
            .Select(key =>
            {
                authPolicyService.ValidatePublicKey(key.PublicKeyText);
                var normalizedPublicKey = authPolicyService.NormalizePublicKey(key.PublicKeyText);
                return key with
                {
                    Label = string.IsNullOrWhiteSpace(key.Label) ? key.Fingerprint : key.Label.Trim(),
                    KeyType = normalizedPublicKey.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0],
                    Fingerprint = authPolicyService.BuildFingerprint(key.PublicKeyText),
                    PublicKeyText = normalizedPublicKey,
                    UpdatedAtUtc = key.UpdatedAtUtc == default ? DateTimeOffset.UtcNow : key.UpdatedAtUtc,
                    CreatedAtUtc = key.CreatedAtUtc == default ? DateTimeOffset.UtcNow : key.CreatedAtUtc
                };
            })
            .DistinctBy(key => key.Fingerprint, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (user.AuthenticationMode != SftpAuthenticationMode.PasswordOnly && publicKeys.Length == 0)
        {
            throw new InvalidOperationException("Public-key authentication requires at least one valid SSH public key.");
        }

        var needsPassword = user.AuthenticationMode is SftpAuthenticationMode.PasswordOnly or SftpAuthenticationMode.PasswordAndPublicKey;
        if (needsPassword &&
            existing is not null &&
            existing.AuthenticationMode == SftpAuthenticationMode.PublicKeyOnly &&
            string.IsNullOrWhiteSpace(newPassword))
        {
            throw new InvalidOperationException("Set a new password when changing this SFTP user away from PublicKeyOnly mode.");
        }

        if (needsPassword && string.IsNullOrWhiteSpace(newPassword) && (existing is null || !existing.HasPassword))
        {
            throw new InvalidOperationException("Set a password for this SFTP user because the selected authentication mode requires one.");
        }

        var folder = chrootService.BuildFolderLayout(basePath, normalizedUserName);
        chrootService.ValidateFolderLayout(folder);

        return new SftpManagedUser(
            normalizedUserName,
            user.AuthenticationMode,
            user.IsEnabled,
            user.AuthenticationMode == SftpAuthenticationMode.PublicKeyOnly ? false : !string.IsNullOrWhiteSpace(newPassword) || existing?.HasPassword == true || user.HasPassword,
            SftpServerDefaults.NoLoginShell,
            SftpServerDefaults.ManagedUsersGroup,
            authPolicyService.GetSupplementaryGroups(user.AuthenticationMode),
            folder,
            publicKeys,
            existing?.CreatedAtUtc ?? DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            !string.IsNullOrWhiteSpace(newPassword)
                ? DateTimeOffset.UtcNow
                : existing?.PasswordChangedAtUtc);
    }

    private IReadOnlyList<SftpConfigurationAction> BuildCreateOrUpdateActions(SftpManagedUser user, bool isCreate, string? newPassword)
    {
        var authGroup = user.SupplementaryGroups.FirstOrDefault() ?? SftpServerDefaults.PublicKeyOnlyGroup;
        var actions = new List<SftpConfigurationAction>
        {
            new(
                1,
                "Ensure SFTP groups",
                "Make sure the base LMS SFTP group and the auth-mode group exist.",
                $"groupadd --system {SftpServerDefaults.ManagedUsersGroup} ; groupadd --system {authGroup}",
                true,
                null,
                null),
            new(
                2,
                isCreate ? "Create Linux account" : "Update Linux account",
                isCreate
                    ? $"Create Linux account '{user.UserName}' with the nologin shell and managed SFTP groups."
                    : $"Update Linux account '{user.UserName}' to keep the nologin shell, managed groups, and SFTP home path.",
                isCreate
                    ? $"useradd -M -g {SftpServerDefaults.ManagedUsersGroup} -G {authGroup} -d {user.Folder.ChrootPath} -s {SftpServerDefaults.NoLoginShell} {user.UserName}"
                    : $"usermod -g {SftpServerDefaults.ManagedUsersGroup} -G {authGroup} -d {user.Folder.ChrootPath} -s {SftpServerDefaults.NoLoginShell} {user.UserName}",
                true,
                null,
                null),
            new(
                3,
                user.IsEnabled ? "Enable SFTP account" : "Disable SFTP account",
                user.IsEnabled
                    ? $"Clear the account expiry date for '{user.UserName}'."
                    : $"Expire '{user.UserName}' immediately so SFTP logins stop.",
                $"usermod --expiredate {(user.IsEnabled ? "-1" : "1")} {user.UserName}",
                true,
                null,
                null),
            new(
                4,
                "Prepare chroot folders",
                $"Create root-owned chroot '{user.Folder.ChrootPath}' and writable folder '{user.Folder.WritablePath}'.",
                $"install -d -m {user.Folder.ChrootMode} {user.Folder.ChrootPath} ; install -d -m {user.Folder.WritableMode} {user.Folder.WritablePath} ; chown root:root {user.Folder.ChrootPath} ; chown {user.UserName}:{SftpServerDefaults.ManagedUsersGroup} {user.Folder.WritablePath}",
                true,
                user.Folder.ChrootPath,
                null),
            new(
                5,
                "Write authorized_keys",
                user.PublicKeys.Count == 0
                    ? $"Remove the managed authorized_keys file for '{user.UserName}'."
                    : $"Install the managed authorized_keys file for '{user.UserName}'.",
                user.PublicKeys.Count == 0
                    ? $"rm -f {SftpServerDefaults.ManagedAuthorizedKeysDirectory}/{user.UserName}"
                    : $"install -m 644 <temp> {SftpServerDefaults.ManagedAuthorizedKeysDirectory}/{user.UserName}",
                true,
                $"{SftpServerDefaults.ManagedAuthorizedKeysDirectory}/{user.UserName}",
                user.PublicKeys.Count == 0 ? null : authPolicyService.BuildAuthorizedKeysFileContents(user.PublicKeys.Select(item => item.PublicKeyText).ToArray()))
        };

        if (!string.IsNullOrWhiteSpace(newPassword) &&
            user.AuthenticationMode != SftpAuthenticationMode.PublicKeyOnly)
        {
            actions.Add(new SftpConfigurationAction(
                actions.Count + 1,
                "Set password",
                $"Apply a new Linux password for '{user.UserName}' without storing the plaintext.",
                "chpasswd <redacted>",
                true,
                null,
                null));
        }
        else if (user.AuthenticationMode == SftpAuthenticationMode.PublicKeyOnly)
        {
            actions.Add(new SftpConfigurationAction(
                actions.Count + 1,
                "Disable password login",
                $"Lock the Linux password for '{user.UserName}' because the account is PublicKeyOnly.",
                $"passwd -l {user.UserName}",
                true,
                null,
                null));
        }

        return actions;
    }

    private async Task EnsureGroupExistsAsync(string groupName, List<OperationLogEntry> logs, CancellationToken cancellationToken)
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
            logs.Add(SftpSystemCommandHelper.MapLog(getent, $"Linux group {groupName} already exists"));
            return;
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
        logs.Add(SftpSystemCommandHelper.MapLog(create, $"Create Linux group {groupName}"));

        if (create.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not create the SFTP group '{groupName}': {SftpSystemCommandHelper.BuildFailureDetail(create)}");
        }
    }

    private async Task EnsureDirectoryAsync(string path, string mode, string description, List<OperationLogEntry> logs, CancellationToken cancellationToken)
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
        logs.Add(SftpSystemCommandHelper.MapLog(create, description));

        if (create.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not prepare '{path}' for SFTP management: {SftpSystemCommandHelper.BuildFailureDetail(create)}");
        }
    }

    private async Task<bool> SystemUserExistsAsync(string userName, CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "id",
                ["-u", userName],
                false,
                CommandTimeout,
                $"Check Linux account {userName}"),
            dryRun: false,
            cancellationToken);
        return result.ExitCode == 0;
    }

    private async Task CreateSystemUserAsync(SftpManagedUser user, List<OperationLogEntry> logs, CancellationToken cancellationToken)
    {
        var authGroup = user.SupplementaryGroups.FirstOrDefault() ?? SftpServerDefaults.PublicKeyOnlyGroup;
        var create = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "useradd",
                ["-M", "-g", user.PrimaryGroup, "-G", authGroup, "-d", user.Folder.ChrootPath, "-s", user.LoginShell, user.UserName],
                true,
                CommandTimeout,
                $"Create Linux SFTP account {user.UserName}"),
            dryRun: false,
            cancellationToken);
        logs.Add(SftpSystemCommandHelper.MapLog(create, $"Create Linux SFTP account {user.UserName}"));

        if (create.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not create the SFTP account '{user.UserName}': {SftpSystemCommandHelper.BuildFailureDetail(create)}");
        }
    }

    private async Task UpdateSystemUserAsync(SftpManagedUser user, List<OperationLogEntry> logs, CancellationToken cancellationToken)
    {
        var authGroup = user.SupplementaryGroups.FirstOrDefault() ?? SftpServerDefaults.PublicKeyOnlyGroup;
        var update = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "usermod",
                ["-g", user.PrimaryGroup, "-G", authGroup, "-d", user.Folder.ChrootPath, "-s", user.LoginShell, user.UserName],
                true,
                CommandTimeout,
                $"Update Linux SFTP account {user.UserName}"),
            dryRun: false,
            cancellationToken);
        logs.Add(SftpSystemCommandHelper.MapLog(update, $"Update Linux SFTP account {user.UserName}"));

        if (update.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not update the SFTP account '{user.UserName}': {SftpSystemCommandHelper.BuildFailureDetail(update)}");
        }
    }

    private async Task SetAccountExpiryAsync(string userName, bool enabled, List<OperationLogEntry> logs, CancellationToken cancellationToken)
    {
        var expire = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "usermod",
                ["--expiredate", enabled ? "-1" : "1", userName],
                true,
                CommandTimeout,
                enabled ? $"Enable Linux account {userName}" : $"Disable Linux account {userName}"),
            dryRun: false,
            cancellationToken);
        logs.Add(SftpSystemCommandHelper.MapLog(expire, enabled ? $"Enable Linux account {userName}" : $"Disable Linux account {userName}"));

        if (expire.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not update the enabled state for '{userName}': {SftpSystemCommandHelper.BuildFailureDetail(expire)}");
        }
    }

    private async Task EnsureChrootFoldersAsync(SftpManagedUser user, List<OperationLogEntry> logs, CancellationToken cancellationToken)
    {
        await EnsureDirectoryAsync(user.Folder.ChrootPath, user.Folder.ChrootMode, $"Ensure chroot path {user.Folder.ChrootPath}", logs, cancellationToken);
        await EnsureDirectoryAsync(user.Folder.WritablePath, user.Folder.WritableMode, $"Ensure writable SFTP path {user.Folder.WritablePath}", logs, cancellationToken);

        var fixChrootOwner = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "chown",
                [$"{user.Folder.ChrootOwner}:{user.Folder.ChrootGroup}", user.Folder.ChrootPath],
                true,
                CommandTimeout,
                $"Set chroot ownership for {user.UserName}"),
            dryRun: false,
            cancellationToken);
        logs.Add(SftpSystemCommandHelper.MapLog(fixChrootOwner, $"Set chroot ownership for {user.UserName}"));

        if (fixChrootOwner.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not set root ownership on '{user.Folder.ChrootPath}': {SftpSystemCommandHelper.BuildFailureDetail(fixChrootOwner)}");
        }

        var fixWritableOwner = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "chown",
                [$"{user.UserName}:{SftpServerDefaults.ManagedUsersGroup}", user.Folder.WritablePath],
                true,
                CommandTimeout,
                $"Set writable folder ownership for {user.UserName}"),
            dryRun: false,
            cancellationToken);
        logs.Add(SftpSystemCommandHelper.MapLog(fixWritableOwner, $"Set writable folder ownership for {user.UserName}"));

        if (fixWritableOwner.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not set ownership on '{user.Folder.WritablePath}': {SftpSystemCommandHelper.BuildFailureDetail(fixWritableOwner)}");
        }
    }

    private async Task ApplyAuthorizedKeysAsync(SftpManagedUser user, List<OperationLogEntry> logs, CancellationToken cancellationToken)
    {
        if (user.PublicKeys.Count == 0)
        {
            await RemoveAuthorizedKeysAsync(user.UserName, logs, cancellationToken);
            return;
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"lms-sftp-authorized-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var tempFile = Path.Combine(tempDirectory, $"{user.UserName}.authorized_keys");
            var fileContents = authPolicyService.BuildAuthorizedKeysFileContents(user.PublicKeys.Select(item => item.PublicKeyText).ToArray());
            await File.WriteAllTextAsync(tempFile, fileContents, cancellationToken);

            var install = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "install",
                    ["-m", "644", tempFile, $"{SftpServerDefaults.ManagedAuthorizedKeysDirectory}/{user.UserName}"],
                    true,
                    CommandTimeout,
                    $"Install authorized_keys for {user.UserName}"),
                dryRun: false,
                cancellationToken);
            logs.Add(SftpSystemCommandHelper.MapLog(install, $"Install authorized_keys for {user.UserName}"));

            if (install.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Linux Made Sane could not install the authorized_keys file for '{user.UserName}': {SftpSystemCommandHelper.BuildFailureDetail(install)}");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // ignore temp cleanup failures
            }
        }
    }

    private async Task RemoveAuthorizedKeysAsync(string userName, List<OperationLogEntry> logs, CancellationToken cancellationToken)
    {
        var remove = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "rm",
                ["-f", $"{SftpServerDefaults.ManagedAuthorizedKeysDirectory}/{userName}"],
                true,
                CommandTimeout,
                $"Remove authorized_keys for {userName}"),
            dryRun: false,
            cancellationToken);
        logs.Add(SftpSystemCommandHelper.MapLog(remove, $"Remove authorized_keys for {userName}"));

        if (remove.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not remove the authorized_keys file for '{userName}': {SftpSystemCommandHelper.BuildFailureDetail(remove)}");
        }
    }

    private async Task ApplyPasswordStateAsync(
        SftpManagedUser user,
        SftpManagedUser? existing,
        string? newPassword,
        List<OperationLogEntry> logs,
        CancellationToken cancellationToken)
    {
        if (user.AuthenticationMode == SftpAuthenticationMode.PublicKeyOnly)
        {
            var lockResult = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "passwd",
                    ["-l", user.UserName],
                    true,
                    CommandTimeout,
                    $"Lock Linux password for {user.UserName}"),
                dryRun: false,
                cancellationToken);
            logs.Add(SftpSystemCommandHelper.MapLog(lockResult, $"Lock Linux password for {user.UserName}"));

            if (lockResult.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Linux Made Sane could not disable password login for '{user.UserName}': {SftpSystemCommandHelper.BuildFailureDetail(lockResult)}");
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(newPassword))
        {
            await SetPasswordAsync(user.UserName, newPassword, logs, cancellationToken);
            return;
        }

        if (existing is not null &&
            existing.HasPassword &&
            existing.AuthenticationMode != SftpAuthenticationMode.PublicKeyOnly)
        {
            return;
        }

        throw new InvalidOperationException("Set a password for this SFTP user because the selected authentication mode requires one.");
    }

    private async Task SetPasswordAsync(string userName, string password, List<OperationLogEntry> logs, CancellationToken cancellationToken)
    {
        var command = Environment.UserName.Equals("root", StringComparison.OrdinalIgnoreCase)
            ? ("chpasswd", Array.Empty<string>())
            : ("sudo", new[] { "-n", "chpasswd" });

        var startInfo = new ProcessStartInfo
        {
            FileName = command.Item1,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in command.Item2)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        await process.StandardInput.WriteLineAsync($"{userName}:{password}");
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        logs.Add(new OperationLogEntry(
            DateTimeOffset.UtcNow,
            process.ExitCode == 0 ? OperationLogLevel.Info : OperationLogLevel.Error,
            $"Set Linux password for {userName}",
            $"{(command.Item1 == "sudo" ? "sudo -n " : string.Empty)}chpasswd <redacted>",
            process.ExitCode,
            stdout,
            stderr));

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not update the password for '{userName}': {SftpSystemCommandHelper.FirstNonEmptyLine(stderr, stdout) ?? $"exit code {process.ExitCode}"}");
        }
    }

    private async Task DeleteSystemUserAsync(string userName, List<OperationLogEntry> logs, CancellationToken cancellationToken)
    {
        var delete = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "bash",
                ["-lc", $"userdel -r {SftpSystemCommandHelper.QuoteShellArgument(userName)} || userdel {SftpSystemCommandHelper.QuoteShellArgument(userName)}"],
                true,
                TimeSpan.FromSeconds(45),
                $"Delete Linux SFTP account {userName}"),
            dryRun: false,
            cancellationToken);
        logs.Add(SftpSystemCommandHelper.MapLog(delete, $"Delete Linux SFTP account {userName}"));

        if (delete.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not delete the SFTP account '{userName}': {SftpSystemCommandHelper.BuildFailureDetail(delete)}");
        }
    }

    private async Task DeleteFolderAsync(SftpManagedUser user, List<OperationLogEntry> logs, CancellationToken cancellationToken)
    {
        var folderPath = user.Folder.ChrootPath;
        if (!folderPath.StartsWith($"{user.Folder.BasePath}/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete a path that is not inside the LMS-managed SFTP root.");
        }

        var delete = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "rm",
                ["-rf", folderPath],
                true,
                TimeSpan.FromSeconds(45),
                $"Delete LMS-managed SFTP directory {folderPath}"),
            dryRun: false,
            cancellationToken);
        logs.Add(SftpSystemCommandHelper.MapLog(delete, $"Delete LMS-managed SFTP directory {folderPath}"));

        if (delete.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not delete the SFTP folder '{folderPath}': {SftpSystemCommandHelper.BuildFailureDetail(delete)}");
        }
    }

    private async Task<SftpManagedUser> RequireExistingUserAsync(string userName, CancellationToken cancellationToken)
    {
        var existing = await store.GetUserAsync(userName, cancellationToken);
        if (existing is null)
        {
            throw new InvalidOperationException($"Linux Made Sane does not have an LMS-managed SFTP user named '{userName}'.");
        }

        return existing;
    }
}
