// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Sftp;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Application.Services;

public sealed class SftpServerManagerService(
    ISftpServerStore store,
    ISftpServerInspectionService inspectionService,
    ISftpServerConfigurationService configurationService,
    ISftpUserManagementService userManagementService) : ISftpServerManagerService
{
    public async Task<SftpServerWorkspaceViewModel> GetWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        var overviewTask = inspectionService.InspectAsync(cancellationToken);
        var usersTask = store.ListUsersAsync(cancellationToken);
        var auditTask = store.ListAuditEntriesAsync(cancellationToken);
        var backupsTask = store.ListBackupSnapshotsAsync(cancellationToken);

        await Task.WhenAll(overviewTask, usersTask, auditTask, backupsTask);

        return new SftpServerWorkspaceViewModel(
            overviewTask.Result,
            usersTask.Result
                .OrderByDescending(user => user.IsEnabled)
                .ThenBy(user => user.UserName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            auditTask.Result
                .OrderByDescending(entry => entry.CreatedAtUtc)
                .ToArray(),
            backupsTask.Result
                .OrderByDescending(snapshot => snapshot.CreatedAtUtc)
                .ToArray());
    }

    public async Task<SftpHostSettingsEditor> GetHostSettingsEditorAsync(CancellationToken cancellationToken = default)
    {
        var settings = await store.GetSettingsAsync(cancellationToken);
        return new SftpHostSettingsEditor
        {
            IsManagedModeEnabled = settings.IsManagedModeEnabled,
            BasePath = settings.BasePath,
            DefaultAuthenticationMode = settings.DefaultAuthenticationMode,
            PreferDropInConfiguration = settings.PreferDropInConfiguration
        };
    }

    public async Task<SftpConfigurationPlan> PreviewHostConfigurationAsync(
        SftpHostSettingsEditor editor,
        CancellationToken cancellationToken = default)
    {
        var existingSettings = await store.GetSettingsAsync(cancellationToken);
        var users = await store.ListUsersAsync(cancellationToken);
        return await configurationService.BuildHostPlanAsync(MapSettings(editor, existingSettings), users, dryRun: true, cancellationToken);
    }

    public async Task<SftpApplyResult> ApplyHostConfigurationAsync(
        SftpHostSettingsEditor editor,
        bool approved,
        CancellationToken cancellationToken = default)
    {
        var existingSettings = await store.GetSettingsAsync(cancellationToken);
        var users = await store.ListUsersAsync(cancellationToken);
        return await configurationService.ApplyHostPlanAsync(MapSettings(editor, existingSettings), users, approved, cancellationToken);
    }

    public async Task<SftpManagedUserEditor> GetUserEditorAsync(
        string? userName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            var settings = await store.GetSettingsAsync(cancellationToken);
            return new SftpManagedUserEditor
            {
                AuthenticationMode = settings.DefaultAuthenticationMode,
                IsEnabled = true
            };
        }

        var user = await store.GetUserAsync(userName, cancellationToken);
        if (user is null)
        {
            return new SftpManagedUserEditor();
        }

        return new SftpManagedUserEditor
        {
            OriginalUserName = user.UserName,
            UserName = user.UserName,
            AuthenticationMode = user.AuthenticationMode,
            IsEnabled = user.IsEnabled,
            HasExistingPassword = user.HasPassword,
            PasswordChangedAtUtc = user.PasswordChangedAtUtc,
            PublicKeys = user.PublicKeys
                .OrderBy(key => key.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(key => key.Fingerprint, StringComparer.OrdinalIgnoreCase)
                .Select(key => new SftpPublicKeyEditor
                {
                    Id = key.Id,
                    Label = key.Label,
                    PublicKeyText = key.PublicKeyText
                })
                .ToList()
        };
    }

    public async Task<SftpConfigurationPlan> PreviewUserAsync(
        SftpManagedUserEditor editor,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var settings = await store.GetSettingsAsync(cancellationToken);
        return await userManagementService.BuildCreateOrUpdatePlanAsync(
            MapUser(editor, settings.BasePath),
            NormalizePassword(password),
            dryRun: true,
            cancellationToken);
    }

    public async Task<SftpApplyResult> SaveUserAsync(
        SftpManagedUserEditor editor,
        string? password,
        bool approved,
        CancellationToken cancellationToken = default)
    {
        var settings = await store.GetSettingsAsync(cancellationToken);
        return await userManagementService.ApplyCreateOrUpdateAsync(
            MapUser(editor, settings.BasePath),
            NormalizePassword(password),
            approved,
            cancellationToken);
    }

    public async Task<SftpUserDetailsViewModel?> GetUserDetailsAsync(
        string userName,
        CancellationToken cancellationToken = default)
    {
        var userTask = store.GetUserAsync(userName, cancellationToken);
        var auditTask = store.ListAuditEntriesAsync(userName, cancellationToken);
        await Task.WhenAll(userTask, auditTask);

        return userTask.Result is null
            ? null
            : new SftpUserDetailsViewModel(
                userTask.Result,
                auditTask.Result
                    .OrderByDescending(entry => entry.CreatedAtUtc)
                    .ToArray());
    }

    public async Task<SftpPasswordResetEditor> GetPasswordResetEditorAsync(
        string userName,
        CancellationToken cancellationToken = default)
    {
        var user = await store.GetUserAsync(userName, cancellationToken);
        if (user is null)
        {
            throw new InvalidOperationException($"Linux Made Sane does not have an LMS-managed SFTP user named '{userName}'.");
        }

        return new SftpPasswordResetEditor
        {
            UserName = user.UserName,
            AuthenticationMode = user.AuthenticationMode,
            NewPassword = GenerateSuggestedPassword()
        };
    }

    public Task<SftpConfigurationPlan> PreviewPasswordResetAsync(
        SftpPasswordResetEditor editor,
        CancellationToken cancellationToken = default) =>
        userManagementService.BuildPasswordResetPlanAsync(
            editor.UserName,
            editor.AuthenticationMode,
            dryRun: true,
            cancellationToken);

    public Task<SftpApplyResult> ResetPasswordAsync(
        SftpPasswordResetEditor editor,
        bool approved,
        CancellationToken cancellationToken = default) =>
        userManagementService.ResetPasswordAsync(
            editor.UserName,
            editor.AuthenticationMode,
            NormalizeRequiredPassword(editor.NewPassword),
            approved,
            cancellationToken);

    public Task<SftpConfigurationPlan> PreviewDisableUserAsync(
        string userName,
        CancellationToken cancellationToken = default) =>
        userManagementService.BuildDisablePlanAsync(userName, dryRun: true, cancellationToken);

    public Task<SftpApplyResult> DisableUserAsync(
        string userName,
        bool approved,
        CancellationToken cancellationToken = default) =>
        userManagementService.DisableAsync(userName, approved, cancellationToken);

    public Task<SftpConfigurationPlan> PreviewDeleteUserAsync(
        string userName,
        CancellationToken cancellationToken = default) =>
        userManagementService.BuildDeletePlanAsync(userName, dryRun: true, cancellationToken);

    public Task<SftpApplyResult> DeleteUserAsync(
        string userName,
        bool approved,
        CancellationToken cancellationToken = default) =>
        userManagementService.DeleteAsync(userName, approved, cancellationToken);

    private static SftpHostSettings MapSettings(SftpHostSettingsEditor editor, SftpHostSettings existingSettings) =>
        new(
            editor.IsManagedModeEnabled,
            NormalizeBasePath(editor.BasePath),
            editor.DefaultAuthenticationMode,
            editor.PreferDropInConfiguration,
            editor.PreferDropInConfiguration
                ? SftpServerDefaults.ManagedDropInPath
                : SftpServerDefaults.MainSshdConfigPath,
            existingSettings.CreatedAtUtc,
            DateTimeOffset.UtcNow,
            existingSettings.LastAppliedAtUtc);

    private static SftpManagedUser MapUser(SftpManagedUserEditor editor, string basePath)
    {
        var normalizedUserName = (editor.UserName ?? string.Empty).Trim().ToLowerInvariant();
        return new SftpManagedUser(
            normalizedUserName,
            editor.AuthenticationMode,
            editor.IsEnabled,
            editor.HasExistingPassword,
            SftpServerDefaults.NoLoginShell,
            SftpServerDefaults.ManagedUsersGroup,
            [],
            BuildPlaceholderFolder(basePath, normalizedUserName),
            editor.PublicKeys
                .Where(static key => !string.IsNullOrWhiteSpace(key.PublicKeyText))
                .Select(key => new SftpPublicKey(
                    key.Id == Guid.Empty ? Guid.NewGuid() : key.Id,
                    (key.Label ?? string.Empty).Trim(),
                    string.Empty,
                    string.Empty,
                    key.PublicKeyText.Trim(),
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow))
                .ToArray(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            editor.PasswordChangedAtUtc);
    }

    private static SftpUserFolder BuildPlaceholderFolder(string basePath, string userName)
    {
        var normalizedBasePath = NormalizeBasePath(basePath);
        return new SftpUserFolder(
            normalizedBasePath,
            $"{normalizedBasePath}/{userName}",
            $"{normalizedBasePath}/{userName}/files",
            "root",
            "root",
            "755",
            userName,
            SftpServerDefaults.ManagedUsersGroup,
            "750");
    }

    private static string NormalizeBasePath(string value)
    {
        var normalized = (value ?? string.Empty).Trim().Replace('\\', '/');
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

    private static string? NormalizePassword(string? password) =>
        string.IsNullOrWhiteSpace(password) ? null : password.Trim();

    private static string NormalizeRequiredPassword(string password)
    {
        var normalized = NormalizePassword(password);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Enter a password before applying this SFTP change.");
        }

        return normalized;
    }

    private static string GenerateSuggestedPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^*-_=+?";
        var all = upper + lower + digits + symbols;
        Span<char> password = stackalloc char[20];

        password[0] = upper[Random.Shared.Next(upper.Length)];
        password[1] = lower[Random.Shared.Next(lower.Length)];
        password[2] = digits[Random.Shared.Next(digits.Length)];
        password[3] = symbols[Random.Shared.Next(symbols.Length)];

        for (var index = 4; index < password.Length; index++)
        {
            password[index] = all[Random.Shared.Next(all.Length)];
        }

        for (var index = password.Length - 1; index > 0; index--)
        {
            var swapIndex = Random.Shared.Next(index + 1);
            (password[index], password[swapIndex]) = (password[swapIndex], password[index]);
        }

        return new string(password);
    }
}
