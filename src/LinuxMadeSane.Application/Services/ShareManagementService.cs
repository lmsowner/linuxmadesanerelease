using LinuxMadeSane.Application.Contracts.Shares;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Application.Services;

public sealed class ShareManagementService(
    ILinuxShareModuleDataService shareDataService,
    IPackageManagementService packageManagementService,
    IServiceManagementService serviceManagementService,
    IManagedHostStore managedHostStore,
    ISshfsMountService? sshfsMountService = null) : IShareManagementService
{
    private static readonly string[] SambaServerPackageNames = ["samba"];
    private static readonly ShareToolingDefinition[] SshfsToolingDefinitions =
    [
        new(
            "sshfs",
            "Lets LMS mount registered SSH hosts directly into the local filesystem.",
            ["sshfs"],
            EnablesNetworkScan: false,
            EnablesRemoteBrowse: false,
            EnablesRemoteMount: true),
        new(
            "fuse3",
            "Provides the FUSE runtime used by modern SSHFS mounts.",
            ["fusermount3"],
            EnablesNetworkScan: false,
            EnablesRemoteBrowse: false,
            EnablesRemoteMount: true)
    ];
    private static readonly ShareToolingDefinition[] ShareToolingDefinitions =
    [
        new(
            "samba-common-bin",
            "Lets LMS validate live Samba configuration with `testparm` before it falls back to raw config parsing.",
            ["testparm"],
            EnablesNetworkScan: false,
            EnablesRemoteBrowse: false,
            EnablesRemoteMount: false),
        new(
            "smbclient",
            "Lets LMS scan the network and interrogate remote SMB machines and their shares.",
            ["smbclient", "smbtree", "findsmb"],
            EnablesNetworkScan: true,
            EnablesRemoteBrowse: true,
            EnablesRemoteMount: false),
        new(
            "avahi-utils",
            "Lets LMS discover Bonjour/mDNS-advertised SMB file servers without brute-force port scanning.",
            ["avahi-browse"],
            EnablesNetworkScan: false,
            EnablesRemoteBrowse: false,
            EnablesRemoteMount: false),
        new(
            "cifs-utils",
            "Lets LMS create temporary and permanent CIFS mounts on the server.",
            ["mount.cifs"],
            EnablesNetworkScan: false,
            EnablesRemoteBrowse: false,
            EnablesRemoteMount: true)
    ];

    public async Task<SharesDashboardViewModel> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var shares = await shareDataService.ListSharesAsync(cancellationToken);
        var presets = await shareDataService.ListPresetsAsync(cancellationToken);
        var defaultPath = shares.FirstOrDefault()?.SharePath ?? "/srv/shares";
        var issues = await shareDataService.ScanIssuesAsync(defaultPath, cancellationToken);

        return new SharesDashboardViewModel(shares, presets, issues.Take(3).ToArray());
    }

    public async Task<ShareManagerViewModel> GetShareManagerAsync(CancellationToken cancellationToken = default)
    {
        var shares = await shareDataService.ListSharesAsync(cancellationToken);
        var systemCheck = await shareDataService.GetSambaSystemCheckAsync(cancellationToken);
        return new ShareManagerViewModel(shares, systemCheck);
    }

    public async Task<UsersGroupsManagerViewModel> GetUsersGroupsManagerAsync(CancellationToken cancellationToken = default)
    {
        var users = await shareDataService.ListUsersAsync(cancellationToken);
        var groups = await shareDataService.ListGroupsAsync(cancellationToken);
        var accessPolicies = await shareDataService.ListUserAccessPoliciesAsync(cancellationToken);

        return new UsersGroupsManagerViewModel(
            users,
            groups,
            accessPolicies
                .OrderBy(policy => policy.UserName, StringComparer.OrdinalIgnoreCase)
                .Select(policy => new LocalUserAccessViewModel(
                    policy.UserName,
                    policy.IsManagedPolicy,
                    policy.IsManagedPolicy ? policy.SshAuthenticationMode : null,
                    policy.IsManagedPolicy && !string.IsNullOrWhiteSpace(policy.AuthorizedKeyEntries),
                    policy.PasswordChangedAtUtc))
                .ToArray());
    }

    public async Task<ShareEditor> GetEditorAsync(Guid? id, CancellationToken cancellationToken = default)
    {
        if (!id.HasValue)
        {
            return new ShareEditor();
        }

        var share = await shareDataService.GetShareAsync(id.Value, cancellationToken);
        return share is null ? new ShareEditor { Id = id } : MapEditor(share);
    }

    public async Task<Guid> SaveShareAsync(ShareEditor editor, CancellationToken cancellationToken = default)
    {
        var shareId = editor.Id ?? Guid.NewGuid();
        var normalizedName = editor.Name.Trim();
        var definition = new SambaShareDefinition(
            shareId,
            normalizedName,
            string.IsNullOrWhiteSpace(editor.SharePath) ? BuildSuggestedSharePath(normalizedName) : editor.SharePath.Trim(),
            string.IsNullOrWhiteSpace(editor.Description) ? BuildDefaultDescription(normalizedName) : editor.Description.Trim(),
            editor.Browseable,
            editor.ReadOnly,
            editor.GuestAccess,
            ParseCsv(editor.ValidUsersCsv),
            ParseCsv(editor.ValidGroupsCsv),
            ParseCsv(editor.WriteListCsv),
            ParseCsv(editor.ReadListCsv),
            NullIfWhiteSpace(editor.ForceUser),
            NullIfWhiteSpace(editor.ForceGroup),
            NormalizeMask(editor.CreateMask, "0664"),
            NormalizeMask(editor.DirectoryMask, "2775"),
            ExplainMask(editor.CreateMask, false),
            ExplainMask(editor.DirectoryMask, true));

        await shareDataService.SaveShareAsync(definition, cancellationToken);
        return shareId;
    }

    public async Task RepairShareAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var share = await shareDataService.GetShareAsync(id, cancellationToken);
        if (share is null)
        {
            throw new InvalidOperationException("The selected share no longer exists.");
        }

        await shareDataService.SaveShareAsync(share, cancellationToken);
    }

    public Task DeleteShareAsync(Guid id, CancellationToken cancellationToken = default) =>
        shareDataService.DeleteShareAsync(id, cancellationToken);

    public async Task<UserEditor> GetUserEditorAsync(Guid? id, CancellationToken cancellationToken = default)
    {
        if (!id.HasValue)
        {
            return new UserEditor();
        }

        var user = await shareDataService.GetUserAsync(id.Value, cancellationToken);
        return user is null ? new UserEditor { Id = id } : MapEditor(user);
    }

    public async Task<Guid> SaveUserAsync(UserEditor editor, CancellationToken cancellationToken = default)
    {
        var userId = editor.Id ?? Guid.NewGuid();
        var userName = NormalizeUserName(editor.UserName);
        var user = new LinuxShareUser(
            userId,
            userName,
            -1,
            -1,
            DefaultIfWhiteSpace(editor.DisplayName, userName),
            DefaultIfWhiteSpace(editor.PrimaryGroup, userName),
            ParseCsv(editor.SupplementaryGroupsCsv),
            DefaultIfWhiteSpace(editor.HomeDirectory, $"/home/{userName}"),
            DefaultIfWhiteSpace(editor.LoginShell, "/bin/bash"),
            editor.IsEnabled);

        await shareDataService.SaveUserAsync(user, cancellationToken);
        return userId;
    }

    public Task DeleteUserAsync(Guid id, CancellationToken cancellationToken = default) =>
        shareDataService.DeleteUserAsync(id, cancellationToken);

    public async Task<LocalUserAccessEditor> GetLocalUserAccessEditorAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await GetRequiredUserAsync(userId, cancellationToken);
        var existingPolicy = await shareDataService.GetUserAccessPolicyAsync(user.UserName, cancellationToken);

        return new LocalUserAccessEditor
        {
            UserName = user.UserName,
            SshAuthenticationMode = existingPolicy?.SshAuthenticationMode ?? RemoteAccessSshAuthenticationMode.Password,
            AuthorizedKeyEntries = existingPolicy?.AuthorizedKeyEntries ?? string.Empty
        };
    }

    public async Task SaveLocalUserAccessAsync(Guid userId, LocalUserAccessEditor editor, CancellationToken cancellationToken = default)
    {
        var user = await GetRequiredUserAsync(userId, cancellationToken);
        var normalizedUserName = NormalizeUserName(user.UserName);

        if (!normalizedUserName.Equals(NormalizeUserName(editor.UserName), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Local user access changes must target the selected Linux user.");
        }

        ValidateAuthorizedKeys(editor.SshAuthenticationMode, editor.AuthorizedKeyEntries);
        var existingPolicy = await shareDataService.GetUserAccessPolicyAsync(normalizedUserName, cancellationToken);

        await shareDataService.SaveUserAccessPolicyAsync(
            new LocalUserAccessPolicy(
                normalizedUserName,
                true,
                editor.SshAuthenticationMode,
                NormalizeAuthorizedKeyEntries(editor.AuthorizedKeyEntries),
                DateTimeOffset.UtcNow,
                existingPolicy?.PasswordChangedAtUtc),
            cancellationToken);
    }

    public async Task<LocalUserPasswordResetViewModel> BuildLocalUserPasswordResetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await GetRequiredUserAsync(userId, cancellationToken);
        return new LocalUserPasswordResetViewModel(user.Id, user.UserName, GenerateSuggestedPassword());
    }

    public async Task ResetLocalUserPasswordAsync(Guid userId, string newPassword, CancellationToken cancellationToken = default)
    {
        var user = await GetRequiredUserAsync(userId, cancellationToken);
        var normalizedPassword = newPassword?.Trim() ?? string.Empty;
        if (normalizedPassword.Length < 14)
        {
            throw new InvalidOperationException("Choose a stronger password with at least 14 characters.");
        }

        await shareDataService.ResetUserPasswordAsync(user.UserName, normalizedPassword, cancellationToken);
    }

    public async Task<GroupEditor> GetGroupEditorAsync(Guid? id, CancellationToken cancellationToken = default)
    {
        if (!id.HasValue)
        {
            return new GroupEditor();
        }

        var group = await shareDataService.GetGroupAsync(id.Value, cancellationToken);
        return group is null ? new GroupEditor { Id = id } : MapEditor(group);
    }

    public async Task<Guid> SaveGroupAsync(GroupEditor editor, CancellationToken cancellationToken = default)
    {
        var groupId = editor.Id ?? Guid.NewGuid();
        var group = new LinuxShareGroup(
            groupId,
            editor.GroupName.Trim(),
            -1,
            DefaultIfWhiteSpace(editor.Description, "Local Linux group"),
            ParseCsv(editor.MembersCsv));

        await shareDataService.SaveGroupAsync(group, cancellationToken);
        return groupId;
    }

    public Task DeleteGroupAsync(Guid id, CancellationToken cancellationToken = default) =>
        shareDataService.DeleteGroupAsync(id, cancellationToken);

    public async Task<PermissionExplainerViewModel> GetPermissionExplainerAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var shares = await shareDataService.ListSharesAsync(cancellationToken);
        var normalizedPath = string.IsNullOrWhiteSpace(path)
            ? shares.FirstOrDefault()?.SharePath ?? "/srv/shares"
            : path.Trim();
        var explanation = await shareDataService.ExplainPermissionsAsync(normalizedPath, cancellationToken);
        return new PermissionExplainerViewModel(normalizedPath, explanation);
    }

    public async Task<EffectiveAccessCheckerViewModel> GetEffectiveAccessCheckerAsync(
        Guid shareId,
        string principal,
        IReadOnlyList<string> groupMembership,
        CancellationToken cancellationToken = default)
    {
        var shares = await shareDataService.ListSharesAsync(cancellationToken);
        var fallbackShareId = shares.FirstOrDefault()?.Id ?? Guid.Empty;
        var selectedShareId = shareId == Guid.Empty ? fallbackShareId : shareId;
        var normalizedPrincipal = string.IsNullOrWhiteSpace(principal) ? "alex" : principal.Trim();
        var groups = groupMembership.Count == 0 ? ["operators"] : groupMembership;
        var result = await shareDataService.CheckEffectiveAccessAsync(selectedShareId, normalizedPrincipal, groups, cancellationToken);

        return new EffectiveAccessCheckerViewModel(shares, result);
    }

    public async Task<OwnershipWizardViewModel> GetWizardAsync(CancellationToken cancellationToken = default)
    {
        var presets = await shareDataService.ListPresetsAsync(cancellationToken);
        return new OwnershipWizardViewModel(presets);
    }

    public async Task<PermissionFixViewModel> GetFixPlanAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var shares = await shareDataService.ListSharesAsync(cancellationToken);
        var normalizedPath = string.IsNullOrWhiteSpace(path)
            ? shares.FirstOrDefault()?.SharePath ?? "/srv/shares"
            : path.Trim();
        var issues = await shareDataService.ScanIssuesAsync(normalizedPath, cancellationToken);
        return new PermissionFixViewModel(normalizedPath, issues);
    }

    public async Task<MountHelperViewModel?> GetMountHelperAsync(
        Guid shareId,
        CancellationToken cancellationToken = default)
    {
        var shares = await shareDataService.ListSharesAsync(cancellationToken);
        var share = shares.FirstOrDefault(item => item.Id == shareId) ?? shares.FirstOrDefault();
        if (share is null)
        {
            return null;
        }

        var recipes = await shareDataService.GetMountRecipesAsync(share.Id, cancellationToken);
        return new MountHelperViewModel(share, recipes);
    }

    public async Task<NetworkShareMountExplorerViewModel> GetNetworkShareMountExplorerAsync(
        NetworkShareDiscoveryScope discoveryScope = NetworkShareDiscoveryScope.Lan,
        CancellationToken cancellationToken = default)
    {
        var tooling = await GetShareToolingStatusAsync(cancellationToken);
        var discovery = tooling.CanScanNetwork
            ? await shareDataService.DiscoverNetworkShareMachinesAsync(discoveryScope, cancellationToken)
            : new NetworkShareMachineDiscoveryResult(
                [],
                "Network SMB discovery is paused until the required Ubuntu share tooling is installed on this LMS host.",
                ["Install the missing share packages below, then rescan the network."],
                discoveryScope);
        var currentMounts = await shareDataService.ListCurrentMountsAsync(cancellationToken);
        var managedMounts = await shareDataService.ListManagedRemoteMountsAsync(cancellationToken);
        return new NetworkShareMountExplorerViewModel(tooling, discovery, currentMounts, managedMounts);
    }

    public async Task<SshfsMountExplorerViewModel> GetSshfsMountExplorerAsync(CancellationToken cancellationToken = default)
    {
        var tooling = await GetSshfsToolingStatusAsync(cancellationToken);
        var service = GetRequiredSshfsMountService();

        return new SshfsMountExplorerViewModel(
            tooling,
            await service.ListHostCandidatesAsync(cancellationToken),
            await service.ListCurrentMountsAsync(cancellationToken),
            await service.ListManagedMountsAsync(cancellationToken));
    }

    public async Task<RemoteShareBrowseResult> BrowseRemoteSharesAsync(
        RemoteShareConnectionEditor editor,
        CancellationToken cancellationToken = default)
    {
        var tooling = await GetShareToolingStatusAsync(cancellationToken);
        if (!tooling.CanBrowseRemoteShares)
        {
            var target = editor.Target.Trim();
            return new RemoteShareBrowseResult(
                target,
                target,
                null,
                !string.IsNullOrWhiteSpace(editor.UserName) ||
                !string.IsNullOrWhiteSpace(editor.Password) ||
                !string.IsNullOrWhiteSpace(editor.Domain),
                false,
                "Remote share browsing is unavailable until the Ubuntu `smbclient` package is installed on this LMS host.",
                Array.Empty<RemoteSambaShare>(),
                [$"Install the missing packages with `{tooling.InstallCommand}` and retry."]);
        }

        return await shareDataService.BrowseRemoteSharesAsync(
            editor.Target.Trim(),
            NullIfWhiteSpace(editor.UserName),
            NullIfWhiteSpace(editor.Password),
            NullIfWhiteSpace(editor.Domain),
            cancellationToken);
    }

    public async Task<RemoteShareMountResult> CreateRemoteShareMountAsync(
        RemoteShareMountEditor editor,
        CancellationToken cancellationToken = default)
    {
        var tooling = await GetShareToolingStatusAsync(cancellationToken);
        if (!tooling.CanCreateRemoteMounts)
        {
            throw new InvalidOperationException(
                "Remote share mounts are unavailable until the Ubuntu `cifs-utils` package is installed on this LMS host.");
        }

        return await shareDataService.CreateRemoteShareMountAsync(
            new RemoteShareMountRequest(
                editor.Target.Trim(),
                NullIfWhiteSpace(editor.RemoteAddress),
                editor.ShareName.Trim(),
                editor.LocalMountPath.Trim(),
                NullIfWhiteSpace(editor.UserName),
                NullIfWhiteSpace(editor.Password),
                NullIfWhiteSpace(editor.Domain),
                editor.PersistOnServer),
            cancellationToken);
    }

    public async Task<RemoteShareMountResult> UpdateManagedRemoteMountAsync(
        Guid id,
        RemoteShareMountEditor editor,
        CancellationToken cancellationToken = default)
    {
        var tooling = await GetShareToolingStatusAsync(cancellationToken);
        if (!tooling.CanCreateRemoteMounts)
        {
            throw new InvalidOperationException(
                "Remote share mounts are unavailable until the Ubuntu `cifs-utils` package is installed on this LMS host.");
        }

        return await shareDataService.UpdateManagedRemoteMountAsync(
            id,
            new RemoteShareMountRequest(
                editor.Target.Trim(),
                NullIfWhiteSpace(editor.RemoteAddress),
                editor.ShareName.Trim(),
                editor.LocalMountPath.Trim(),
                NullIfWhiteSpace(editor.UserName),
                NullIfWhiteSpace(editor.Password),
                NullIfWhiteSpace(editor.Domain),
                PersistOnServer: true),
            editor.KeepSavedPassword,
            cancellationToken);
    }

    public async Task<SshfsMountResult> CreateSshfsMountAsync(
        SshfsMountEditor editor,
        CancellationToken cancellationToken = default)
    {
        var tooling = await GetSshfsToolingStatusAsync(cancellationToken);
        if (!tooling.CanCreateSshfsMounts)
        {
            throw new InvalidOperationException(
                "SSHFS mounts are unavailable until the Ubuntu `sshfs` and `fuse3` packages are installed on this LMS host.");
        }

        return await GetRequiredSshfsMountService().CreateMountAsync(
            new SshfsMountRequest(
                editor.HostId,
                editor.RemotePath.Trim(),
                editor.LocalMountPath.Trim(),
                editor.PersistOnServer),
            cancellationToken);
    }

    public async Task<ShareToolingInstallResult> InstallMissingShareToolingAsync(CancellationToken cancellationToken = default)
    {
        var tooling = await GetShareToolingStatusAsync(cancellationToken);
        if (!tooling.IsLocalHostRegistered || string.IsNullOrWhiteSpace(tooling.LocalHostName))
        {
            throw new InvalidOperationException(
                "Auto-install is unavailable because the LMS local machine is not registered in host inventory.");
        }

        if (tooling.MissingPackageNames.Count == 0)
        {
            return new ShareToolingInstallResult(
                true,
                tooling.LocalHostName,
                "The registered LMS local machine already has the required share tooling installed.",
                Array.Empty<string>(),
                []);
        }

        var actions = tooling.MissingPackageNames
            .Select(packageName => new PackageAction(
                PackageActionKind.Install,
                packageName,
                "Required for the LMS SMB discovery, browsing, and mount workflow.",
                IsDestructive: false,
                $"apt-get install -y {packageName}"))
            .ToArray();

        var logs = await packageManagementService.ApplyActionsAsync(actions, dryRun: false, cancellationToken);
        var success = logs.All(log => log.Level != OperationLogLevel.Error);

        return new ShareToolingInstallResult(
            success,
            tooling.LocalHostName,
            success
                ? $"Installed {tooling.MissingPackageNames.Count} missing share package(s) on {tooling.LocalHostName}."
                : $"Package installation on {tooling.LocalHostName} needs review. Check the operation log.",
            tooling.MissingPackageNames,
            logs);
    }

    public async Task<ShareToolingInstallResult> InstallMissingSshfsToolingAsync(CancellationToken cancellationToken = default)
    {
        var tooling = await GetSshfsToolingStatusAsync(cancellationToken);
        if (!tooling.IsLocalHostRegistered || string.IsNullOrWhiteSpace(tooling.LocalHostName))
        {
            throw new InvalidOperationException(
                "Auto-install is unavailable because the LMS local machine is not registered in host inventory.");
        }

        if (tooling.MissingPackageNames.Count == 0)
        {
            return new ShareToolingInstallResult(
                true,
                tooling.LocalHostName,
                "The registered LMS local machine already has SSHFS and FUSE installed.",
                Array.Empty<string>(),
                []);
        }

        var actions = tooling.MissingPackageNames
            .Select(packageName => new PackageAction(
                PackageActionKind.Install,
                packageName,
                "Required for LMS SSHFS host mounts.",
                IsDestructive: false,
                $"apt-get install -y {packageName}"))
            .ToArray();

        var logs = await packageManagementService.ApplyActionsAsync(actions, dryRun: false, cancellationToken);
        var success = logs.All(log => log.Level != OperationLogLevel.Error);

        return new ShareToolingInstallResult(
            success,
            tooling.LocalHostName,
            success
                ? $"Installed {tooling.MissingPackageNames.Count} missing SSHFS package(s) on {tooling.LocalHostName}."
                : $"SSHFS package installation on {tooling.LocalHostName} needs review. Check the operation log.",
            tooling.MissingPackageNames,
            logs);
    }

    public async Task<SambaSystemActionResult> RunSambaSystemActionAsync(
        SambaSystemAction action,
        CancellationToken cancellationToken = default)
    {
        var logs = action switch
        {
            SambaSystemAction.Start => await ApplySambaServiceActionsAsync(
                [
                    new ServiceAction(
                        ServiceActionKind.Start,
                        "smbd",
                        "Bring Samba online for Windows SMB clients.",
                        IsDestructive: false,
                        "systemctl start smbd")
                ],
                cancellationToken),
            SambaSystemAction.Stop => await ApplySambaServiceActionsAsync(
                [
                    new ServiceAction(
                        ServiceActionKind.Stop,
                        "smbd",
                        "Take Samba offline on this LMS host.",
                        IsDestructive: true,
                        "systemctl stop smbd")
                ],
                cancellationToken),
            SambaSystemAction.Restart => await ApplySambaServiceActionsAsync(
                [
                    new ServiceAction(
                        ServiceActionKind.Restart,
                        "smbd",
                        "Reload Samba by restarting the Windows SMB service.",
                        IsDestructive: false,
                        "systemctl restart smbd")
                ],
                cancellationToken),
            SambaSystemAction.Fix => await ApplySambaFixAsync(cancellationToken),
            _ => []
        };

        var systemCheck = await shareDataService.GetSambaSystemCheckAsync(cancellationToken);
        var outcome = BuildSambaActionOutcome(action, logs, systemCheck);
        return new SambaSystemActionResult(
            action,
            outcome.Success,
            outcome.Message,
            outcome.Tone,
            systemCheck,
            logs);
    }

    public Task DeleteManagedRemoteMountAsync(Guid id, CancellationToken cancellationToken = default) =>
        shareDataService.DeleteManagedRemoteMountAsync(id, cancellationToken);

    public Task DeleteManagedSshfsMountAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetRequiredSshfsMountService().DeleteManagedMountAsync(id, cancellationToken);

    public Task CleanupTemporaryRemoteMountsAsync(CancellationToken cancellationToken = default) =>
        shareDataService.CleanupTemporaryRemoteMountsAsync(cancellationToken);

    public Task<bool> ReleaseTemporaryRemoteMountAsync(string localMountPath, CancellationToken cancellationToken = default) =>
        shareDataService.ReleaseTemporaryRemoteMountAsync(localMountPath, cancellationToken);

    public Task<bool> DisconnectCurrentRemoteMountAsync(string localMountPath, CancellationToken cancellationToken = default) =>
        shareDataService.DisconnectCurrentRemoteMountAsync(localMountPath, cancellationToken);

    private async Task<IReadOnlyList<OperationLogEntry>> ApplySambaServiceActionsAsync(
        IReadOnlyList<ServiceAction> actions,
        CancellationToken cancellationToken) =>
        await serviceManagementService.ApplyActionsAsync(actions, dryRun: false, cancellationToken);

    private async Task<IReadOnlyList<OperationLogEntry>> ApplySambaFixAsync(CancellationToken cancellationToken)
    {
        var logs = new List<OperationLogEntry>();
        var packageStates = await packageManagementService.InspectAsync(SambaServerPackageNames, cancellationToken);
        var missingPackages = packageStates
            .Where(package => !package.IsInstalled)
            .Select(package => package.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (missingPackages.Length > 0)
        {
            logs.AddRange(await packageManagementService.ApplyActionsAsync(
                missingPackages
                    .Select(packageName => new PackageAction(
                        PackageActionKind.Install,
                        packageName,
                        "Required to expose LMS-managed shares through the Samba server service.",
                        IsDestructive: false,
                        $"apt-get install -y {packageName}"))
                    .ToArray(),
                dryRun: false,
                cancellationToken));
        }

        if (logs.Any(log => log.Level == OperationLogLevel.Error))
        {
            return logs;
        }

        var currentCheck = await shareDataService.GetSambaSystemCheckAsync(cancellationToken);
        var configCheck = currentCheck.Checks.FirstOrDefault(check => check.Key == "config");
        if (configCheck is { Severity: PermissionIssueSeverity.Critical })
        {
            logs.Add(new OperationLogEntry(
                DateTimeOffset.UtcNow,
                OperationLogLevel.Error,
                "Samba configuration is invalid, so LMS did not enable or restart `smbd`.",
                "testparm -s",
                null,
                configCheck.Detail,
                null));
            return logs;
        }

        foreach (var shareStatus in currentCheck.ShareStatuses.Where(CanAutoRepairShare))
        {
            var share = await shareDataService.GetShareAsync(shareStatus.ShareId, cancellationToken);
            if (share is null)
            {
                continue;
            }

            try
            {
                await shareDataService.SaveShareAsync(share, cancellationToken);
                logs.Add(new OperationLogEntry(
                    DateTimeOffset.UtcNow,
                    OperationLogLevel.Success,
                    $"Repaired LMS share {share.Name}.",
                    null,
                    0,
                    "Re-exported the LMS share into live Samba configuration.",
                    null));
            }
            catch (Exception exception)
            {
                logs.Add(new OperationLogEntry(
                    DateTimeOffset.UtcNow,
                    OperationLogLevel.Error,
                    $"Repairing LMS share {share.Name} failed.",
                    null,
                    null,
                    null,
                    exception.Message));
                return logs;
            }
        }

        logs.AddRange(await ApplySambaServiceActionsAsync(
            [
                new ServiceAction(
                    ServiceActionKind.Enable,
                    "smbd",
                    "Make sure Samba starts automatically after reboot.",
                    IsDestructive: false,
                    "systemctl enable smbd"),
                new ServiceAction(
                    ServiceActionKind.Restart,
                    "smbd",
                    "Reload Samba after install or config review so Windows clients can connect.",
                    IsDestructive: false,
                    "systemctl restart smbd")
            ],
            cancellationToken));

        return logs;
    }

    private static bool CanAutoRepairShare(SambaShareRuntimeStatus shareStatus) =>
        shareStatus.Status.Equals("Not live", StringComparison.OrdinalIgnoreCase);

    private static ShareEditor MapEditor(SambaShareDefinition share) =>
        new()
        {
            Id = share.Id,
            Name = share.Name,
            SharePath = share.SharePath,
            Description = share.Description,
            Browseable = share.Browseable,
            ReadOnly = share.ReadOnly,
            GuestAccess = share.GuestAccess,
            ValidUsersCsv = string.Join(", ", share.ValidUsers),
            ValidGroupsCsv = string.Join(", ", share.ValidGroups),
            WriteListCsv = string.Join(", ", share.WriteList),
            ReadListCsv = string.Join(", ", share.ReadList),
            ForceUser = share.ForceUser ?? string.Empty,
            ForceGroup = share.ForceGroup ?? string.Empty,
            CreateMask = share.CreateMask,
            DirectoryMask = share.DirectoryMask
        };

    private static UserEditor MapEditor(LinuxShareUser user) =>
        new()
        {
            Id = user.Id,
            UserName = user.UserName,
            DisplayName = user.DisplayName,
            PrimaryGroup = user.PrimaryGroup,
            SupplementaryGroupsCsv = string.Join(", ", user.SupplementaryGroups),
            HomeDirectory = user.HomeDirectory,
            LoginShell = user.LoginShell,
            IsEnabled = user.IsEnabled
        };

    private static GroupEditor MapEditor(LinuxShareGroup group) =>
        new()
        {
            Id = group.Id,
            GroupName = group.GroupName,
            Description = group.Description,
            MembersCsv = string.Join(", ", group.Members)
        };

    private static IReadOnlyList<string> ParseCsv(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeUserName(string value) =>
        value.Trim().ToLowerInvariant();

    private static void ValidateAuthorizedKeys(RemoteAccessSshAuthenticationMode mode, string authorizedKeyEntries)
    {
        if (mode is not RemoteAccessSshAuthenticationMode.Password &&
            string.IsNullOrWhiteSpace(authorizedKeyEntries))
        {
            throw new InvalidOperationException("Key-based SSH modes require at least one imported OpenSSH public key or certificate-authority entry.");
        }
    }

    private static string NormalizeAuthorizedKeyEntries(string value) =>
        string.Join(
            Environment.NewLine,
            value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string DefaultIfWhiteSpace(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

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

    private static (bool Success, string Tone, string Message) BuildSambaActionOutcome(
        SambaSystemAction action,
        IReadOnlyList<OperationLogEntry> logs,
        SambaShareSystemCheckResult systemCheck)
    {
        var errorLog = logs.FirstOrDefault(log => log.Level == OperationLogLevel.Error);
        if (errorLog is not null)
        {
            return (
                false,
                "error",
                string.IsNullOrWhiteSpace(errorLog.StandardError)
                    ? errorLog.Message
                    : $"{errorLog.Message} {errorLog.StandardError.Trim()}");
        }

        var smbdCheck = systemCheck.Checks.FirstOrDefault(check => check.Key == "smbd");
        var configCheck = systemCheck.Checks.FirstOrDefault(check => check.Key == "config");
        var portCheck = systemCheck.Checks.FirstOrDefault(check => check.Key == "port-445");
        var remainingShareFindingCount = CountShareSpecificFindings(systemCheck);

        return action switch
        {
            SambaSystemAction.Start => IsSmbdRunning(smbdCheck)
                ? BuildRunningOutcome(
                    "Started `smbd`.",
                    smbdCheck,
                    portCheck,
                    remainingShareFindingCount)
                : (false, "error", smbdCheck?.Detail ?? "LMS could not confirm that `smbd` started."),
            SambaSystemAction.Stop => !IsSmbdRunning(smbdCheck)
                ? (true, "warning", "Stopped `smbd`. Windows SMB access from this LMS host is now offline.")
                : (false, "error", smbdCheck?.Detail ?? "LMS could not confirm that `smbd` stopped."),
            SambaSystemAction.Restart => IsSmbdRunning(smbdCheck)
                ? BuildRunningOutcome(
                    "Restarted `smbd`.",
                    smbdCheck,
                    portCheck,
                    remainingShareFindingCount)
                : (false, "error", smbdCheck?.Detail ?? "LMS could not confirm that `smbd` restarted."),
            SambaSystemAction.Fix => BuildFixOutcome(configCheck, smbdCheck, portCheck, remainingShareFindingCount),
            _ => (true, "info", "Samba action completed.")
        };
    }

    private static (bool Success, string Tone, string Message) BuildRunningOutcome(
        string prefix,
        SambaShareServiceCheck? smbdCheck,
        SambaShareServiceCheck? portCheck,
        int remainingShareFindingCount)
    {
        if (!IsSmbdRunning(smbdCheck))
        {
            return (false, "error", smbdCheck?.Detail ?? "The Samba service is not running.");
        }

        if (portCheck?.IsHealthy != true)
        {
            return (false, "error", portCheck?.Detail ?? "TCP 445 is still not listening after the Samba action.");
        }

        if (remainingShareFindingCount > 0)
        {
            return (
                true,
                "warning",
                $"{prefix} The host runtime looks reachable, but {remainingShareFindingCount} share definition(s) still need attention.");
        }

        return smbdCheck?.Severity == PermissionIssueSeverity.Warning
            ? (true, "warning", $"{prefix} `smbd` is running, but boot enablement still needs review.")
            : (true, "success", $"{prefix} Windows clients should now be able to reach TCP 445 on this LMS host.");
    }

    private static (bool Success, string Tone, string Message) BuildFixOutcome(
        SambaShareServiceCheck? configCheck,
        SambaShareServiceCheck? smbdCheck,
        SambaShareServiceCheck? portCheck,
        int remainingShareFindingCount)
    {
        if (configCheck?.IsHealthy != true)
        {
            return (false, "error", configCheck?.Detail ?? "Samba configuration still needs review.");
        }

        if (!IsSmbdRunning(smbdCheck))
        {
            return (false, "error", smbdCheck?.Detail ?? "The Samba service is still not running.");
        }

        if (portCheck?.IsHealthy != true)
        {
            return (false, "error", portCheck?.Detail ?? "TCP 445 is still not listening after the repair attempt.");
        }

        if (remainingShareFindingCount > 0)
        {
            return (
                true,
                "warning",
                $"Samba runtime now looks healthy, but {remainingShareFindingCount} share definition(s) still need manual follow-up.");
        }

        return (true, "success", "Samba runtime looks healthy. Config validates, `smbd` is running, and TCP 445 is listening.");
    }

    private static bool IsSmbdRunning(SambaShareServiceCheck? check) =>
        check?.IsHealthy == true;

    private static int CountShareSpecificFindings(SambaShareSystemCheckResult systemCheck) =>
        systemCheck.ShareStatuses.Count(status => !status.IsHealthy);

    private async Task<LinuxShareUser> GetRequiredUserAsync(Guid id, CancellationToken cancellationToken) =>
        await shareDataService.GetUserAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("The selected Linux user no longer exists.");

    private ISshfsMountService GetRequiredSshfsMountService() =>
        sshfsMountService ?? throw new InvalidOperationException("SSHFS mount support is not available in this LMS build.");

    private async Task<SshfsToolingStatusViewModel> GetSshfsToolingStatusAsync(CancellationToken cancellationToken)
    {
        var packageStates = await packageManagementService.InspectAsync(
            SshfsToolingDefinitions.Select(item => item.PackageName).ToArray(),
            cancellationToken);

        var packageStatesByName = packageStates.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        var packages = SshfsToolingDefinitions
            .Select(definition =>
            {
                var packageState = packageStatesByName.GetValueOrDefault(definition.PackageName);
                return new ShareToolingPackageViewModel(
                    definition.PackageName,
                    definition.Purpose,
                    definition.Commands,
                    packageState?.IsInstalled == true,
                    packageState?.Version ?? "-");
            })
            .ToArray();

        var missingPackageNames = packages
            .Where(item => !item.IsInstalled)
            .Select(item => item.PackageName)
            .ToArray();
        var canCreateSshfsMounts = IsFeatureEnabled(SshfsToolingDefinitions, packages, definition => definition.EnablesRemoteMount);
        var notes = new List<string>();
        if (!canCreateSshfsMounts)
        {
            notes.Add("SSHFS mounts stay disabled until `sshfs` and `fuse3` are installed on the LMS host.");
        }

        notes.Add("Only registered hosts with stored public key authentication are available for SSHFS mounts.");
        notes.Add("Automated SSHFS mounts require a non-interactive private key. Use a dedicated mount key if the normal admin key has a passphrase.");

        var installPackages = missingPackageNames.Length == 0
            ? SshfsToolingDefinitions.Select(item => item.PackageName).ToArray()
            : missingPackageNames;

        var localHost = await managedHostStore.GetAsync(AiLocalMachine.ManagedHostId, cancellationToken);
        var statusMessage = missingPackageNames.Length == 0
            ? "The LMS host has SSHFS and FUSE installed for registered SSH host mounts."
            : $"This LMS host is missing {missingPackageNames.Length} package(s) required for SSHFS mounts.";

        return new SshfsToolingStatusViewModel(
            missingPackageNames.Length == 0,
            canCreateSshfsMounts,
            localHost is not null,
            localHost?.Name,
            statusMessage,
            $"sudo apt-get update && sudo apt-get install -y -- {string.Join(' ', installPackages)}",
            missingPackageNames,
            notes,
            packages);
    }

    private async Task<ShareToolingStatusViewModel> GetShareToolingStatusAsync(CancellationToken cancellationToken)
    {
        var packageStates = await packageManagementService.InspectAsync(
            ShareToolingDefinitions.Select(item => item.PackageName).ToArray(),
            cancellationToken);

        var packageStatesByName = packageStates.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        var packages = ShareToolingDefinitions
            .Select(definition =>
            {
                var packageState = packageStatesByName.GetValueOrDefault(definition.PackageName);
                return new ShareToolingPackageViewModel(
                    definition.PackageName,
                    definition.Purpose,
                    definition.Commands,
                    packageState?.IsInstalled == true,
                    packageState?.Version ?? "-");
            })
            .ToArray();

        var missingPackageNames = packages
            .Where(item => !item.IsInstalled)
            .Select(item => item.PackageName)
            .ToArray();

        var canScanNetwork = IsFeatureEnabled(ShareToolingDefinitions, packages, definition => definition.EnablesNetworkScan);
        var canBrowseRemoteShares = IsFeatureEnabled(ShareToolingDefinitions, packages, definition => definition.EnablesRemoteBrowse);
        var canCreateRemoteMounts = IsFeatureEnabled(ShareToolingDefinitions, packages, definition => definition.EnablesRemoteMount);

        var notes = new List<string>();
        if (!packages.Any(item => item.PackageName.Equals("samba-common-bin", StringComparison.OrdinalIgnoreCase) && item.IsInstalled))
        {
            notes.Add("Live Samba validation falls back to reading `smb.conf` files directly because `testparm` is missing.");
        }

        if (!canBrowseRemoteShares)
        {
            notes.Add("Network scanning and remote share interrogation stay disabled until `smbclient` is installed.");
        }

        if (!packages.Any(item => item.PackageName.Equals("avahi-utils", StringComparison.OrdinalIgnoreCase) && item.IsInstalled))
        {
            notes.Add("mDNS/Bonjour SMB discovery is unavailable until `avahi-utils` is installed; LMS can still use Samba browser discovery and manual targets when `smbclient` is present.");
        }

        if (!canCreateRemoteMounts)
        {
            notes.Add("Temporary and permanent CIFS mounts stay disabled until `cifs-utils` is installed.");
        }

        var installPackages = missingPackageNames.Length == 0
            ? ShareToolingDefinitions.Select(item => item.PackageName).ToArray()
            : missingPackageNames;

        var localHost = await managedHostStore.GetAsync(AiLocalMachine.ManagedHostId, cancellationToken);
        var statusMessage = missingPackageNames.Length == 0
            ? "The LMS host has the expected Ubuntu share tooling installed for remote SMB discovery, browsing, and CIFS mounts."
            : $"This LMS host is missing {missingPackageNames.Length} Ubuntu share package(s) required for full SMB discovery, interrogation, and mount workflows.";

        return new ShareToolingStatusViewModel(
            missingPackageNames.Length == 0,
            canScanNetwork,
            canBrowseRemoteShares,
            canCreateRemoteMounts,
            localHost is not null,
            localHost?.Name,
            statusMessage,
            $"sudo apt-get update && sudo apt-get install -y -- {string.Join(' ', installPackages)}",
            missingPackageNames,
            notes,
            packages);
    }

    private static bool IsFeatureEnabled(
        IReadOnlyList<ShareToolingDefinition> definitions,
        IReadOnlyList<ShareToolingPackageViewModel> packages,
        Func<ShareToolingDefinition, bool> selector) =>
        definitions
            .Where(selector)
            .All(definition => packages.Any(item =>
                item.PackageName.Equals(definition.PackageName, StringComparison.OrdinalIgnoreCase) &&
                item.IsInstalled));

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeMask(string value, string fallback)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? fallback : trimmed;
    }

    private static string ExplainMask(string value, bool directory)
    {
        var trimmed = NormalizeMask(value, directory ? "2775" : "0664");
        var octal = trimmed.TrimStart('0');
        if (!int.TryParse(octal, out var parsed))
        {
            return directory
                ? "Directory inheritance needs review because the mask is not a standard octal value."
                : "File permissions need review because the mask is not a standard octal value.";
        }

        var owner = DescribePermissionDigit((parsed / 100) % 10);
        var group = DescribePermissionDigit((parsed / 10) % 10);
        var others = DescribePermissionDigit(parsed % 10);
        var noun = directory ? "New folders" : "New files";

        return $"{noun} grant {owner} to owner, {group} to group, and {others} to everyone else.";
    }

    private static string DescribePermissionDigit(int value) => value switch
    {
        7 => "full access",
        6 => "read and write",
        5 => "read and enter",
        4 => "read only",
        3 => "write and enter",
        2 => "write only",
        1 => "enter only",
        _ => "no access"
    };

    private static string BuildSuggestedSharePath(string shareName)
    {
        var slugBuilder = new System.Text.StringBuilder();
        var previousWasSeparator = false;

        foreach (var character in shareName.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                slugBuilder.Append(character);
                previousWasSeparator = false;
                continue;
            }

            if (previousWasSeparator)
            {
                continue;
            }

            slugBuilder.Append('-');
            previousWasSeparator = true;
        }

        var slug = slugBuilder.ToString().Trim('-');
        if (slug.Length == 0)
        {
            slug = "share";
        }

        return $"/srv/shares/{slug}";
    }

    private static string BuildDefaultDescription(string shareName) =>
        $"{shareName} shared files";

    private sealed record ShareToolingDefinition(
        string PackageName,
        string Purpose,
        IReadOnlyList<string> Commands,
        bool EnablesNetworkScan,
        bool EnablesRemoteBrowse,
        bool EnablesRemoteMount);
}
