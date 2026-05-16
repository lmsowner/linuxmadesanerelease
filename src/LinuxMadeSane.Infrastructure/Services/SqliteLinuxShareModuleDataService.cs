// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Shares;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SqliteLinuxShareModuleDataService : ILinuxShareModuleDataService
{
    private readonly LinuxMadeSaneDbContext dbContext;
    private readonly ILinuxCommandRunner commandRunner;
    private readonly SambaConfigurationShareReader sambaConfigurationShareReader;
    private readonly SambaConfigurationShareWriter sambaConfigurationShareWriter;
    private readonly SambaNetworkDiscoveryService sambaNetworkDiscoveryService;
    private readonly SambaRemoteMountService sambaRemoteMountService;
    private readonly ILocalUserAccessSystemService localUserAccessSystemService;

    public SqliteLinuxShareModuleDataService(
        LinuxMadeSaneDbContext dbContext,
        ILinuxCommandRunner commandRunner,
        ILocalUserAccessSystemService localUserAccessSystemService,
        ShareMountStorageSettings shareMountStorageSettings)
        : this(
            dbContext,
            commandRunner,
            localUserAccessSystemService,
            new SambaConfigurationShareReader(commandRunner),
            new SambaConfigurationShareWriter(commandRunner),
            new SambaNetworkDiscoveryService(commandRunner),
            new SambaRemoteMountService(dbContext, commandRunner, shareMountStorageSettings))
    {
    }

    internal SqliteLinuxShareModuleDataService(
        LinuxMadeSaneDbContext dbContext,
        ILinuxCommandRunner commandRunner,
        SambaConfigurationShareReader sambaConfigurationShareReader)
        : this(
            dbContext,
            commandRunner,
            new LocalUserAccessSystemService(commandRunner),
            sambaConfigurationShareReader,
            new SambaConfigurationShareWriter(commandRunner),
            new SambaNetworkDiscoveryService(commandRunner),
            new SambaRemoteMountService(
                dbContext,
                commandRunner,
                new ShareMountStorageSettings(Path.Combine(Path.GetTempPath(), "linuxmadesane-tests", "share-mounts"))))
    {
    }

    internal SqliteLinuxShareModuleDataService(
        LinuxMadeSaneDbContext dbContext,
        ILinuxCommandRunner commandRunner,
        SambaConfigurationShareReader sambaConfigurationShareReader,
        SambaConfigurationShareWriter sambaConfigurationShareWriter)
        : this(
            dbContext,
            commandRunner,
            new LocalUserAccessSystemService(commandRunner),
            sambaConfigurationShareReader,
            sambaConfigurationShareWriter,
            new SambaNetworkDiscoveryService(commandRunner),
            new SambaRemoteMountService(
                dbContext,
                commandRunner,
                new ShareMountStorageSettings(Path.Combine(Path.GetTempPath(), "linuxmadesane-tests", "share-mounts"))))
    {
    }

    internal SqliteLinuxShareModuleDataService(
        LinuxMadeSaneDbContext dbContext,
        ILinuxCommandRunner commandRunner,
        ILocalUserAccessSystemService localUserAccessSystemService,
        SambaConfigurationShareReader sambaConfigurationShareReader,
        SambaConfigurationShareWriter sambaConfigurationShareWriter,
        SambaNetworkDiscoveryService sambaNetworkDiscoveryService,
        SambaRemoteMountService sambaRemoteMountService)
    {
        this.dbContext = dbContext;
        this.commandRunner = commandRunner;
        this.localUserAccessSystemService = localUserAccessSystemService;
        this.sambaConfigurationShareReader = sambaConfigurationShareReader;
        this.sambaConfigurationShareWriter = sambaConfigurationShareWriter;
        this.sambaNetworkDiscoveryService = sambaNetworkDiscoveryService;
        this.sambaRemoteMountService = sambaRemoteMountService;
    }

    public async Task<IReadOnlyList<SambaShareDefinition>> ListSharesAsync(CancellationToken cancellationToken = default)
    {
        await SyncCurrentSharesAsync(cancellationToken);

        var items = await dbContext.SambaShares
            .AsNoTracking()
            .OrderBy(share => share.Name)
            .ToListAsync(cancellationToken);

        return items.Select(Map).ToArray();
    }

    public async Task<SambaShareDefinition?> GetShareAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await ListSharesAsync(cancellationToken)).FirstOrDefault(share => share.Id == id);

    public async Task SaveShareAsync(SambaShareDefinition share, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SambaShares
            .SingleOrDefaultAsync(existing => existing.Id == share.Id, cancellationToken);
        var previousShare = entity is null ? null : Map(entity);
        var isLiveShare = await IsLiveSambaShareAsync(share, cancellationToken);
        var isManagedLiveShare = previousShare is not null &&
                                 await sambaConfigurationShareWriter.IsManagedShareAsync(previousShare, cancellationToken);

        if (isLiveShare && !isManagedLiveShare)
        {
            throw new InvalidOperationException(
                "This share comes from the current Samba configuration and is not LMS-managed. Edit it in Samba directly or import it into the LMS-managed share file first.");
        }

        await sambaConfigurationShareWriter.SaveManagedShareAsync(share, previousShare, cancellationToken);

        if (entity is null)
        {
            dbContext.SambaShares.Add(Map(share));
        }
        else
        {
            entity.Name = share.Name;
            entity.SharePath = share.SharePath;
            entity.Description = share.Description;
            entity.Browseable = share.Browseable;
            entity.ReadOnly = share.ReadOnly;
            entity.GuestAccess = share.GuestAccess;
            entity.ValidUsersJson = SerializeList(share.ValidUsers);
            entity.ValidGroupsJson = SerializeList(share.ValidGroups);
            entity.WriteListJson = SerializeList(share.WriteList);
            entity.ReadListJson = SerializeList(share.ReadList);
            entity.ForceUser = share.ForceUser;
            entity.ForceGroup = share.ForceGroup;
            entity.CreateMask = share.CreateMask;
            entity.DirectoryMask = share.DirectoryMask;
            entity.CreateMaskExplanation = share.CreateMaskExplanation;
            entity.DirectoryMaskExplanation = share.DirectoryMaskExplanation;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteShareAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SambaShares.SingleOrDefaultAsync(share => share.Id == id, cancellationToken);
        if (entity is null)
        {
            return;
        }

        var share = Map(entity);
        var isManagedLiveShare = await sambaConfigurationShareWriter.IsManagedShareAsync(share, cancellationToken);
        if (await IsLiveSambaShareAsync(share, cancellationToken) && !isManagedLiveShare)
        {
            throw new InvalidOperationException(
                "This share comes from the current Samba configuration and is not LMS-managed. Remove it from Samba instead of deleting the local record here.");
        }

        if (isManagedLiveShare)
        {
            await sambaConfigurationShareWriter.DeleteManagedShareAsync(share, cancellationToken);
        }

        dbContext.SambaShares.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LinuxShareUser>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        var groups = await ReadSystemGroupEntriesAsync(cancellationToken);
        var primaryGroupByGid = groups
            .GroupBy(group => group.Gid)
            .ToDictionary(group => group.Key, group => group.First().GroupName);

        var users = (await ReadSystemUserEntriesAsync(cancellationToken))
            .Select(user =>
            {
                var primaryGroup = primaryGroupByGid.TryGetValue(user.Gid, out var groupName)
                    ? groupName
                    : user.UserName;

                var supplementaryGroups = groups
                    .Where(group => group.Members.Contains(user.UserName, StringComparer.OrdinalIgnoreCase) &&
                                    !group.GroupName.Equals(primaryGroup, StringComparison.OrdinalIgnoreCase))
                    .Select(group => group.GroupName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var displayName = string.IsNullOrWhiteSpace(user.DisplayName)
                    ? user.UserName
                    : user.DisplayName;

                return new LinuxShareUser(
                    CreateStableId("user", user.UserName),
                    user.UserName,
                    user.Uid,
                    user.Gid,
                    displayName,
                    primaryGroup,
                    supplementaryGroups,
                    user.HomeDirectory,
                    user.LoginShell,
                    !IsNonInteractiveShell(user.LoginShell));
            })
            .OrderByDescending(user => IsLikelyHumanUser(user))
            .ThenBy(user => user.UserName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return users;
    }

    public async Task<LinuxShareUser?> GetUserAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await ListUsersAsync(cancellationToken)).FirstOrDefault(user => user.Id == id);

    public async Task SaveUserAsync(LinuxShareUser user, CancellationToken cancellationToken = default)
    {
        var normalizedUserName = user.UserName.Trim().ToLowerInvariant();
        var displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? normalizedUserName : user.DisplayName.Trim();
        var primaryGroup = string.IsNullOrWhiteSpace(user.PrimaryGroup) ? normalizedUserName : user.PrimaryGroup.Trim();
        var homeDirectory = string.IsNullOrWhiteSpace(user.HomeDirectory) ? $"/home/{normalizedUserName}" : user.HomeDirectory.Trim();
        var loginShell = string.IsNullOrWhiteSpace(user.LoginShell) ? "/bin/bash" : user.LoginShell.Trim();
        var supplementaryGroups = user.SupplementaryGroups
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var existingUser = await GetUserAsync(user.Id, cancellationToken) ??
                           (await ListUsersAsync(cancellationToken))
                           .FirstOrDefault(existing => existing.UserName.Equals(normalizedUserName, StringComparison.OrdinalIgnoreCase));

        if (existingUser is null)
        {
            if (!primaryGroup.Equals(normalizedUserName, StringComparison.OrdinalIgnoreCase))
            {
                await EnsureGroupExistsAsync(primaryGroup, cancellationToken);
            }

            var args = new List<string> { "-m", "-c", displayName, "-d", homeDirectory, "-s", loginShell };
            if (primaryGroup.Equals(normalizedUserName, StringComparison.OrdinalIgnoreCase))
            {
                args.Add("-U");
            }
            else
            {
                args.Add("-g");
                args.Add(primaryGroup);
            }

            args.Add(normalizedUserName);
            await RunRequiredCommandAsync("useradd", args, $"Create Linux user {normalizedUserName}", requiresSudo: true, cancellationToken);
        }
        else
        {
            var currentUserName = existingUser.UserName;
            if (!primaryGroup.Equals(existingUser.PrimaryGroup, StringComparison.OrdinalIgnoreCase))
            {
                await EnsureGroupExistsAsync(primaryGroup, cancellationToken);
            }

            await RunRequiredCommandAsync(
                "usermod",
                ["-c", displayName, "-d", homeDirectory, "-s", loginShell, "-g", primaryGroup, currentUserName],
                $"Update Linux user {currentUserName}",
                requiresSudo: true,
                cancellationToken);
        }

        if (supplementaryGroups.Length > 0)
        {
            await RunRequiredCommandAsync(
                "usermod",
                ["-G", string.Join(",", supplementaryGroups), normalizedUserName],
                $"Set supplementary groups for {normalizedUserName}",
                requiresSudo: true,
                cancellationToken);
        }

        await RunRequiredCommandAsync(
            "usermod",
            [user.IsEnabled ? "-U" : "-L", normalizedUserName],
            user.IsEnabled ? $"Unlock Linux user {normalizedUserName}" : $"Lock Linux user {normalizedUserName}",
            requiresSudo: true,
            cancellationToken);
    }

    public async Task DeleteUserAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await GetUserAsync(id, cancellationToken);
        if (user is null)
        {
            return;
        }

        await RunRequiredCommandAsync(
            "userdel",
            [user.UserName],
            $"Delete Linux user {user.UserName}",
            requiresSudo: true,
            cancellationToken);
    }

    public async Task<IReadOnlyList<LocalUserAccessPolicy>> ListUserAccessPoliciesAsync(CancellationToken cancellationToken = default)
    {
        var items = await dbContext.LocalUserAccessPolicies
            .AsNoTracking()
            .OrderBy(item => item.UserName)
            .ToListAsync(cancellationToken);

        return items.Select(Map).ToArray();
    }

    public async Task<LocalUserAccessPolicy?> GetUserAccessPolicyAsync(string userName, CancellationToken cancellationToken = default)
    {
        var normalizedUserName = userName.Trim().ToLowerInvariant();
        var entity = await dbContext.LocalUserAccessPolicies
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserName == normalizedUserName, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task SaveUserAccessPolicyAsync(LocalUserAccessPolicy policy, CancellationToken cancellationToken = default)
    {
        var normalizedUserName = policy.UserName.Trim().ToLowerInvariant();
        var entity = await dbContext.LocalUserAccessPolicies
            .SingleOrDefaultAsync(item => item.UserName == normalizedUserName, cancellationToken);

        if (entity is null)
        {
            dbContext.LocalUserAccessPolicies.Add(new LocalUserAccessPolicyEntity
            {
                UserName = normalizedUserName,
                IsManagedPolicy = policy.IsManagedPolicy,
                SshAuthenticationMode = (int)policy.SshAuthenticationMode,
                AuthorizedKeyEntries = policy.AuthorizedKeyEntries,
                UpdatedAtUtc = policy.UpdatedAtUtc,
                PasswordChangedAtUtc = policy.PasswordChangedAtUtc
            });
        }
        else
        {
            entity.IsManagedPolicy = policy.IsManagedPolicy;
            entity.SshAuthenticationMode = (int)policy.SshAuthenticationMode;
            entity.AuthorizedKeyEntries = policy.AuthorizedKeyEntries;
            entity.UpdatedAtUtc = policy.UpdatedAtUtc;
            entity.PasswordChangedAtUtc = policy.PasswordChangedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await ApplyUserAccessPoliciesAsync(cancellationToken);
    }

    public async Task ResetUserPasswordAsync(string userName, string newPassword, CancellationToken cancellationToken = default)
    {
        var normalizedUserName = userName.Trim().ToLowerInvariant();
        await localUserAccessSystemService.ResetPasswordAsync(normalizedUserName, newPassword, cancellationToken);

        var entity = await dbContext.LocalUserAccessPolicies
            .SingleOrDefaultAsync(item => item.UserName == normalizedUserName, cancellationToken);

        if (entity is null)
        {
            dbContext.LocalUserAccessPolicies.Add(new LocalUserAccessPolicyEntity
            {
                UserName = normalizedUserName,
                IsManagedPolicy = false,
                SshAuthenticationMode = (int)RemoteAccessSshAuthenticationMode.Password,
                AuthorizedKeyEntries = string.Empty,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                PasswordChangedAtUtc = DateTimeOffset.UtcNow
            });
        }
        else
        {
            entity.PasswordChangedAtUtc = DateTimeOffset.UtcNow;
            entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LinuxShareGroup>> ListGroupsAsync(CancellationToken cancellationToken = default)
    {
        var groups = (await ReadSystemGroupEntriesAsync(cancellationToken))
            .Select(group => new LinuxShareGroup(
                CreateStableId("group", group.GroupName),
                group.GroupName,
                group.Gid,
                group.Members.Count > 0 ? $"Members: {group.Members.Count}" : "Local Linux group",
                group.Members))
            .OrderByDescending(group => group.Members.Count > 0)
            .ThenBy(group => group.GroupName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return groups;
    }

    public async Task<LinuxShareGroup?> GetGroupAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await ListGroupsAsync(cancellationToken)).FirstOrDefault(group => group.Id == id);

    public async Task SaveGroupAsync(LinuxShareGroup group, CancellationToken cancellationToken = default)
    {
        var groupName = group.GroupName.Trim();
        var desiredMembers = group.Members
            .Where(member => !string.IsNullOrWhiteSpace(member))
            .Select(member => member.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var existingGroup = await GetGroupAsync(group.Id, cancellationToken) ??
                            (await ListGroupsAsync(cancellationToken))
                            .FirstOrDefault(existing => existing.GroupName.Equals(groupName, StringComparison.OrdinalIgnoreCase));

        if (existingGroup is null)
        {
            await RunRequiredCommandAsync(
                "groupadd",
                [groupName],
                $"Create Linux group {groupName}",
                requiresSudo: true,
                cancellationToken);
        }

        var currentUsers = await ListUsersAsync(cancellationToken);
        var currentSupplementaryMembers = currentUsers
            .Where(user => user.SupplementaryGroups.Contains(groupName, StringComparer.OrdinalIgnoreCase))
            .Select(user => user.UserName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var member in desiredMembers.Except(currentSupplementaryMembers, StringComparer.OrdinalIgnoreCase))
        {
            await RunRequiredCommandAsync(
                "usermod",
                ["-a", "-G", groupName, member],
                $"Add {member} to group {groupName}",
                requiresSudo: true,
                cancellationToken);
        }

        foreach (var member in currentSupplementaryMembers.Except(desiredMembers, StringComparer.OrdinalIgnoreCase))
        {
            await RunRequiredCommandAsync(
                "gpasswd",
                ["-d", member, groupName],
                $"Remove {member} from group {groupName}",
                requiresSudo: true,
                cancellationToken);
        }
    }

    public async Task DeleteGroupAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var group = await GetGroupAsync(id, cancellationToken);
        if (group is null)
        {
            return;
        }

        await RunRequiredCommandAsync(
            "groupdel",
            [group.GroupName],
            $"Delete Linux group {group.GroupName}",
            requiresSudo: true,
            cancellationToken);
    }

    public async Task<PermissionExplainerResult> ExplainPermissionsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var shares = await ListSharesAsync(cancellationToken);
        var share = MatchShare(shares, path);

        if (share is null)
        {
            return new PermissionExplainerResult(
                path,
                "unknown",
                "unknown",
                "---------",
                false,
                "No configured share matches this path yet, so Linux Made Sane cannot explain the policy with confidence.",
                false,
                false,
                false,
                false,
                false,
                ["Add or select a share definition that owns this path before trusting any fix plan."]);
        }

        var hasAclEntries = share.ValidUsers.Count > 0 || share.ValidGroups.Count > 0 || share.WriteList.Count > 0 || share.ReadList.Count > 0;
        var groupWritable = HasWriteForGroup(share.CreateMask);
        var browseable = share.Browseable && !share.GuestAccess;
        var canRead = share.GuestAccess || share.ReadList.Count > 0 || share.ValidGroups.Count > 0 || share.ValidUsers.Count > 0;
        var canWrite = !share.ReadOnly && (groupWritable || share.WriteList.Count > 0);
        var canCreate = canWrite;
        var canDelete = canWrite && !share.DirectoryMask.StartsWith("1", StringComparison.Ordinal);
        var canRename = canWrite;

        return new PermissionExplainerResult(
            path,
            share.ForceUser ?? "share-owner",
            share.ForceGroup ?? "share-group",
            BuildDirectoryMode(share.DirectoryMask),
            hasAclEntries,
            BuildPlainEnglishSummary(share),
            canRead,
            canWrite,
            canCreate,
            canDelete,
            canRename,
            BuildConflicts(share));
    }

    public async Task<EffectiveAccessCheckResult> CheckEffectiveAccessAsync(
        Guid shareId,
        string principal,
        IReadOnlyList<string> groupMembership,
        CancellationToken cancellationToken = default)
    {
        var share = await GetShareAsync(shareId, cancellationToken);
        if (share is null)
        {
            return new EffectiveAccessCheckResult(principal, groupMembership, shareId, false, false, false, false, false,
                ["The selected share no longer exists."]);
        }

        var normalizedGroups = groupMembership
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var isNamedUser = share.ValidUsers.Contains(principal, StringComparer.OrdinalIgnoreCase);
        var isAllowedGroup = normalizedGroups.Any(group => share.ValidGroups.Contains(group, StringComparer.OrdinalIgnoreCase));
        var isAllowed = share.GuestAccess || isNamedUser || isAllowedGroup || share.ValidUsers.Count == 0 && share.ValidGroups.Count == 0;
        var hasWriteOverride = MatchesPrincipalOrGroup(share.WriteList, principal, normalizedGroups);
        var hasReadOverride = MatchesPrincipalOrGroup(share.ReadList, principal, normalizedGroups);
        var canAccess = isAllowed || hasReadOverride || hasWriteOverride;
        var canList = canAccess && share.Browseable;
        var canCreate = canAccess && !share.ReadOnly && (hasWriteOverride || share.WriteList.Count == 0);
        var canDeleteOwn = canCreate;
        var canDeleteOthers = canCreate && !share.DirectoryMask.StartsWith("1", StringComparison.Ordinal);

        return new EffectiveAccessCheckResult(
            principal,
            normalizedGroups,
            share.Id,
            canAccess,
            canList,
            canCreate,
            canDeleteOwn,
            canDeleteOthers,
            BuildAccessReasons(share, principal, normalizedGroups, canAccess, canCreate, canDeleteOthers, hasReadOverride, hasWriteOverride));
    }

    public Task<IReadOnlyList<OwnershipWizardPreset>> ListPresetsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<OwnershipWizardPreset> presets =
        [
            new(SharePresetType.TeamSharedFolder, "Team shared folder", "All users in a group can edit and new files stay group-owned.", "Best for collaborative departments or project teams.",
                ["Set the directory group to the team group.", "Apply setgid on directories so new items inherit the group.", "Keep Samba write rules and Linux modes aligned."]),
            new(SharePresetType.DropFolder, "Drop folder", "Users can upload but should not browse or remove each other's files.", "Best for intake, vendor uploads, or submission folders.",
                ["Restrict browsing in Samba.", "Use a sticky directory mask to limit deletes.", "Force ownership when uploads must land consistently."]),
            new(SharePresetType.ReadOnlyDepartment, "Read-only department share", "Most users read, a smaller writer set updates content safely.", "Best for policies, templates, or distribution shares.",
                ["Keep the share read-only by default.", "Limit the write list to maintainers.", "Use plain-English descriptions so the policy is obvious."]),
            new(SharePresetType.MediaLibrary, "Media library", "Broad read access with controlled write and cleanup rights.", "Best for shared assets, media archives, and catalogued content.",
                ["Keep write privileges limited to admins.", "Use a stable media group.", "Generate mount recipes that preserve predictable ownership."])
        ];

        return Task.FromResult(presets);
    }

    public async Task<IReadOnlyList<PermissionIssue>> ScanIssuesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var shares = await ListSharesAsync(cancellationToken);
        var share = MatchShare(shares, path);
        if (share is null)
        {
            return
            [
                new PermissionIssue(
                    PermissionIssueSeverity.Critical,
                    "No share definition matches this path",
                    "The path is outside the current managed share catalog, so policy checks are guesswork.",
                    "Create a share definition first, then rerun the scan.",
                    "Trying to fix permissions without a share model usually creates drift.")
            ];
        }

        var issues = new List<PermissionIssue>();

        if (!share.ReadOnly && !HasWriteForGroup(share.CreateMask) && share.WriteList.Count == 0)
        {
            issues.Add(new PermissionIssue(
                PermissionIssueSeverity.Critical,
                "Share is writable in theory but the file mask blocks collaborative editing",
                "The share is not marked read-only, but new files will not grant group write access and there is no explicit write list.",
                "Use a file mask like 0664 or add a precise write list.",
                "Users will see partial write failures and assume Samba is broken."));
        }

        if (!share.ReadOnly && string.IsNullOrWhiteSpace(share.ForceGroup) && share.ValidGroups.Count > 0)
        {
            issues.Add(new PermissionIssue(
                PermissionIssueSeverity.Warning,
                "Collaborative share has no forced group",
                "New uploads may land in the wrong group when users connect from different accounts or clients.",
                "Set a force group or guarantee setgid ownership on the backing path.",
                "Mixed ownership is one of the fastest ways to create permission roulette."));
        }

        if (share.GuestAccess && (share.ValidUsers.Count > 0 || share.ValidGroups.Count > 0))
        {
            issues.Add(new PermissionIssue(
                PermissionIssueSeverity.Warning,
                "Guest access conflicts with restricted allow lists",
                "The share exposes guest access while also trying to restrict specific users or groups.",
                "Choose one policy: guest-style broad access or explicit membership control.",
                "Conflicting access models make support answers unreliable."));
        }

        if (!share.Browseable && !share.DirectoryMask.StartsWith("1", StringComparison.Ordinal))
        {
            issues.Add(new PermissionIssue(
                PermissionIssueSeverity.Info,
                "Hidden share is not using a protective directory mask",
                "Browsing is disabled in Samba, but the directory policy does not clearly reinforce the intake-style behavior.",
                "Consider a sticky or restricted directory mask when the goal is upload-only handling.",
                "Users may still be able to remove or overwrite content more broadly than intended."));
        }

        if (issues.Count == 0)
        {
            issues.Add(new PermissionIssue(
                PermissionIssueSeverity.Info,
                "No obvious policy drift detected",
                "The share definition is internally consistent based on the current Linux Made Sane heuristics.",
                "Validate the backing filesystem ownership on the machine before calling it done.",
                "This module validates the policy model, not live Samba state."));
        }

        return issues;
    }

    public async Task<IReadOnlyList<MountRecipe>> GetMountRecipesAsync(
        Guid shareId,
        CancellationToken cancellationToken = default)
    {
        var share = await GetShareAsync(shareId, cancellationToken);
        if (share is null)
        {
            return Array.Empty<MountRecipe>();
        }

        var unc = $"//linuxmadesane/{share.Name}";
        var writableHint = share.ReadOnly ? "readonly=true" : "readonly=false";
        var localMount = $"/mnt/{share.Name}";

        return
        [
            new MountRecipe(
                MountTargetType.LinuxClient,
                "Linux client mount",
                $"sudo mount -t cifs {unc} {localMount} -o username=<user>,uid=1000,gid=1000,vers=3.1.1,file_mode={share.CreateMask},dir_mode={share.DirectoryMask}",
                "Mount for a normal Linux workstation while keeping the local ownership display predictable.",
                [$"`file_mode` mirrors the share's file mask of `{share.CreateMask}`.", $"`dir_mode` mirrors the directory mask of `{share.DirectoryMask}`."]),
            new MountRecipe(
                MountTargetType.UbuntuDesktop,
                "Ubuntu desktop fstab entry",
                $"{unc} {localMount} cifs credentials=/etc/samba/{share.Name}.cred,uid=1000,gid=1000,iocharset=utf8,vers=3.1.1,nofail 0 0",
                "Auto-mount on an Ubuntu desktop without blocking boot if the share is offline.",
                ["Use a credentials file instead of embedding secrets in `/etc/fstab`.", "`nofail` keeps the machine bootable when the share is unavailable."]),
            new MountRecipe(
                MountTargetType.ServerMount,
                "Server-side mount",
                $"sudo mount -t cifs {unc} /srv/mounts/{share.Name} -o credentials=/root/.smb-{share.Name},vers=3.1.1,noperm,serverino,{writableHint}",
                "Good when another Linux service needs the share mounted on the host.",
                ["`noperm` avoids local client-side confusion when Samba should make the final access decision.", $"This share is currently {(share.ReadOnly ? "read-heavy" : "writable")} by design."]),
            new MountRecipe(
                MountTargetType.DockerContainer,
                "Docker/container mount",
                $"docker run --mount type=bind,src=/srv/mounts/{share.Name},dst=/data/{share.Name},readonly={(share.ReadOnly ? "true" : "false")} <image>",
                "Mount the CIFS share on the host first, then bind it into the container cleanly.",
                ["Do not mount CIFS from inside the container unless you absolutely need to.", "Keep host and container UID/GID assumptions aligned."])
        ];
    }

    public async Task<SambaShareSystemCheckResult> GetSambaSystemCheckAsync(
        CancellationToken cancellationToken = default)
    {
        var shares = await ListSharesAsync(cancellationToken);
        var liveShares = await sambaConfigurationShareReader.ListSharesAsync(cancellationToken);

        var checks = new[]
        {
            await InspectSambaConfigurationAsync(cancellationToken),
            await InspectSmbdServiceAsync(cancellationToken),
            await InspectPort445Async(cancellationToken),
            await InspectFirewallAsync(cancellationToken)
        };

        var findings = new List<ShareSystemCheckFinding>();
        if (shares.Count == 0)
        {
            findings.Add(new ShareSystemCheckFinding(
                PermissionIssueSeverity.Info,
                "No shares are currently defined",
                "LMS did not find any live Samba shares and no local LMS-only share definitions are saved yet.",
                "Create a share or import an existing Samba configuration before testing from Windows."));
        }

        AddCheckFinding(
            checks,
            "config",
            findings,
            "Samba configuration needs review",
            "Fix `smb.conf` validation errors before testing Windows access.");
        AddCheckFinding(
            checks,
            "smbd",
            findings,
            "The SMB sharing service is not ready",
            "Start and enable `smbd` so Windows clients can reach the shares.");
        AddCheckFinding(
            checks,
            "port-445",
            findings,
            "SMB port 445 is not listening",
            "Make sure `smbd` is running and bound to TCP port 445.");
        AddCheckFinding(
            checks,
            "ufw",
            findings,
            "Firewall rules may block Windows access",
            "Allow Samba or TCP 445 through the host firewall and retry from Windows.");

        var shareStatuses = shares
            .Select(share => BuildShareRuntimeStatus(share, liveShares))
            .OrderByDescending(status => status.Severity)
            .ThenBy(status => status.ShareName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var lmsOnlyShares = shareStatuses
            .Where(status => status.Findings.Any(finding =>
                finding.Title.Contains("LMS only", StringComparison.OrdinalIgnoreCase)))
            .Select(status => status.ShareName)
            .ToArray();

        if (lmsOnlyShares.Length > 0)
        {
            findings.Add(new ShareSystemCheckFinding(
                PermissionIssueSeverity.Critical,
                "Saved LMS shares are not live in Samba",
                lmsOnlyShares.Length == 1
                    ? $"`{lmsOnlyShares[0]}` is saved in LMS only, so Windows cannot see it yet."
                    : $"{lmsOnlyShares.Length} shares are saved in LMS only, so Windows cannot see them yet.",
                "Open the share in LMS and save it again so LMS can write it into the managed Samba config, then rerun the system check."));
        }

        var missingPathShares = shareStatuses
            .Where(status => status.Findings.Any(finding =>
                finding.Title.Contains("path does not exist", StringComparison.OrdinalIgnoreCase)))
            .Select(status => status.ShareName)
            .ToArray();

        if (missingPathShares.Length > 0)
        {
            findings.Add(new ShareSystemCheckFinding(
                PermissionIssueSeverity.Critical,
                "One or more share folders do not exist",
                missingPathShares.Length == 1
                    ? $"`{missingPathShares[0]}` points at a missing directory."
                    : $"{missingPathShares.Length} shares point at missing directories.",
                "Create the directory path on disk or correct the share path before testing access."));
        }

        var criticalCount = findings.Count(finding => finding.Severity == PermissionIssueSeverity.Critical) +
                            shareStatuses.Count(status => status.Severity == PermissionIssueSeverity.Critical);
        var warningCount = findings.Count(finding => finding.Severity == PermissionIssueSeverity.Warning) +
                           shareStatuses.Count(status => status.Severity == PermissionIssueSeverity.Warning);

        return new SambaShareSystemCheckResult(
            DateTimeOffset.UtcNow,
            BuildSystemCheckSummary(shares.Count, shareStatuses, criticalCount, warningCount),
            criticalCount == 0 && warningCount == 0,
            checks,
            findings,
            shareStatuses);
    }

    public Task<NetworkShareMachineDiscoveryResult> DiscoverNetworkShareMachinesAsync(
        NetworkShareDiscoveryScope scope = NetworkShareDiscoveryScope.Lan,
        CancellationToken cancellationToken = default) =>
        sambaNetworkDiscoveryService.DiscoverMachinesAsync(scope, cancellationToken);

    public Task<RemoteShareBrowseResult> BrowseRemoteSharesAsync(
        string target,
        string? userName,
        string? password,
        string? domain,
        CancellationToken cancellationToken = default) =>
        sambaNetworkDiscoveryService.BrowseRemoteSharesAsync(target, userName, password, domain, cancellationToken);

    public Task<IReadOnlyList<ManagedRemoteShareMount>> ListManagedRemoteMountsAsync(
        CancellationToken cancellationToken = default) =>
        sambaRemoteMountService.ListManagedMountsAsync(cancellationToken);

    public Task<IReadOnlyList<CurrentSystemMount>> ListCurrentMountsAsync(
        CancellationToken cancellationToken = default) =>
        sambaRemoteMountService.ListCurrentMountsAsync(cancellationToken);

    public Task<RemoteShareMountResult> CreateRemoteShareMountAsync(
        RemoteShareMountRequest request,
        CancellationToken cancellationToken = default) =>
        sambaRemoteMountService.CreateRemoteShareMountAsync(request, cancellationToken);

    public Task<RemoteShareMountResult> UpdateManagedRemoteMountAsync(
        Guid id,
        RemoteShareMountRequest request,
        bool keepSavedPassword,
        CancellationToken cancellationToken = default) =>
        sambaRemoteMountService.UpdateManagedRemoteMountAsync(id, request, keepSavedPassword, cancellationToken);

    public Task<RemoteShareMountResult?> ReconnectManagedRemoteMountAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        sambaRemoteMountService.ReconnectManagedRemoteMountAsync(id, cancellationToken);

    public Task CleanupTemporaryRemoteMountsAsync(CancellationToken cancellationToken = default) =>
        sambaRemoteMountService.CleanupTemporaryMountsAsync(cancellationToken);

    public Task<bool> ReleaseTemporaryRemoteMountAsync(
        string localMountPath,
        CancellationToken cancellationToken = default) =>
        sambaRemoteMountService.ReleaseTemporaryMountAsync(localMountPath, cancellationToken);

    public Task<bool> DisconnectCurrentRemoteMountAsync(
        string localMountPath,
        CancellationToken cancellationToken = default) =>
        sambaRemoteMountService.DisconnectCurrentRemoteMountAsync(localMountPath, cancellationToken);

    public Task DeleteManagedRemoteMountAsync(Guid id, CancellationToken cancellationToken = default) =>
        sambaRemoteMountService.DeleteManagedRemoteMountAsync(id, cancellationToken);

    private async Task<SambaShareServiceCheck> InspectSambaConfigurationAsync(CancellationToken cancellationToken)
    {
        var result = await RunDiagnosticCommandAsync(
            "testparm",
            ["-s"],
            "Validate Samba configuration",
            cancellationToken);

        if (result.ExitCode == 0)
        {
            return new SambaShareServiceCheck(
                "config",
                "Config",
                true,
                PermissionIssueSeverity.Info,
                "Valid",
                "Samba accepted the current configuration.");
        }

        if (IsExecutableMissing(result))
        {
            return new SambaShareServiceCheck(
                "config",
                "Config",
                false,
                PermissionIssueSeverity.Warning,
                "Validation unavailable",
                "The `testparm` command is missing, so LMS cannot verify the live Samba configuration before Windows testing.");
        }

        return new SambaShareServiceCheck(
            "config",
            "Config",
            false,
            PermissionIssueSeverity.Critical,
            "Invalid",
            SummarizeCommandFailure(result, "Samba configuration validation failed."));
    }

    private async Task<SambaShareServiceCheck> InspectSmbdServiceAsync(CancellationToken cancellationToken)
    {
        var activeResult = await RunDiagnosticCommandAsync(
            "systemctl",
            ["is-active", "smbd"],
            "Check SMB service state",
            cancellationToken);

        if (IsExecutableMissing(activeResult))
        {
            return new SambaShareServiceCheck(
                "smbd",
                "SMB service",
                false,
                PermissionIssueSeverity.Warning,
                "Unknown",
                "The `systemctl` command is unavailable here, so LMS cannot confirm whether `smbd` is running.");
        }

        if (IsSystemdUnitMissing(activeResult))
        {
            return new SambaShareServiceCheck(
                "smbd",
                "SMB service",
                false,
                PermissionIssueSeverity.Critical,
                "Not installed",
                "The `smbd` unit was not found. Install the Ubuntu `samba` server package, then enable and start `smbd`.");
        }

        var activeState = activeResult.StandardOutput.Trim();
        if (!string.Equals(activeState, "active", StringComparison.OrdinalIgnoreCase))
        {
            return new SambaShareServiceCheck(
                "smbd",
                "SMB service",
                false,
                PermissionIssueSeverity.Critical,
                "Stopped",
                string.IsNullOrWhiteSpace(activeState)
                    ? SummarizeCommandFailure(activeResult, "The `smbd` service is not active.")
                    : $"The `smbd` service reported `{activeState}` instead of `active`.");
        }

        var enabledResult = await RunDiagnosticCommandAsync(
            "systemctl",
            ["is-enabled", "smbd"],
            "Check SMB service boot state",
            cancellationToken);
        var enabledState = enabledResult.StandardOutput.Trim();

        if (string.Equals(enabledState, "enabled", StringComparison.OrdinalIgnoreCase))
        {
            return new SambaShareServiceCheck(
                "smbd",
                "SMB service",
                true,
                PermissionIssueSeverity.Info,
                "Running",
                "`smbd` is active and enabled for boot.");
        }

        return new SambaShareServiceCheck(
            "smbd",
            "SMB service",
            true,
            PermissionIssueSeverity.Warning,
            "Running / boot review",
            string.IsNullOrWhiteSpace(enabledState)
                ? "`smbd` is active now, but LMS could not confirm whether it starts automatically after reboot."
                : $"`smbd` is active now, but `systemctl is-enabled` returned `{enabledState}`.");
    }

    private async Task<SambaShareServiceCheck> InspectPort445Async(CancellationToken cancellationToken)
    {
        var result = await RunDiagnosticCommandAsync(
            "ss",
            ["-ltn"],
            "Check SMB listen ports",
            cancellationToken);

        if (IsExecutableMissing(result))
        {
            return new SambaShareServiceCheck(
                "port-445",
                "Port 445",
                false,
                PermissionIssueSeverity.Warning,
                "Unknown",
                "The `ss` command is unavailable here, so LMS cannot confirm that Windows can reach TCP 445.");
        }

        if (result.ExitCode != 0)
        {
            return new SambaShareServiceCheck(
                "port-445",
                "Port 445",
                false,
                PermissionIssueSeverity.Warning,
                "Check failed",
                SummarizeCommandFailure(result, "LMS could not inspect listening TCP ports."));
        }

        return IsPort445Listening(result.StandardOutput)
            ? new SambaShareServiceCheck(
                "port-445",
                "Port 445",
                true,
                PermissionIssueSeverity.Info,
                "Listening",
                "The host is listening on TCP 445 for SMB traffic.")
            : new SambaShareServiceCheck(
                "port-445",
                "Port 445",
                false,
                PermissionIssueSeverity.Critical,
                "Closed",
                "Nothing is listening on TCP 445, so modern Windows clients will not reach Samba shares on this host.");
    }

    private async Task<SambaShareServiceCheck> InspectFirewallAsync(CancellationToken cancellationToken)
    {
        var result = await RunDiagnosticCommandAsync(
            "ufw",
            ["status"],
            "Check firewall status",
            cancellationToken);

        if (IsExecutableMissing(result))
        {
            return new SambaShareServiceCheck(
                "ufw",
                "Firewall",
                true,
                PermissionIssueSeverity.Info,
                "Unmanaged",
                "UFW is not installed, so LMS cannot warn about UFW-specific SMB blocks.");
        }

        var output = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;

        if (string.IsNullOrWhiteSpace(output))
        {
            return new SambaShareServiceCheck(
                "ufw",
                "Firewall",
                true,
                PermissionIssueSeverity.Info,
                "Unknown",
                "No firewall status output was returned.");
        }

        if (output.Contains("Status: inactive", StringComparison.OrdinalIgnoreCase))
        {
            return new SambaShareServiceCheck(
                "ufw",
                "Firewall",
                true,
                PermissionIssueSeverity.Info,
                "Inactive",
                "UFW is inactive, so it is not blocking Samba traffic.");
        }

        if (output.Contains("Status: active", StringComparison.OrdinalIgnoreCase) &&
            FirewallAllowsSamba(output))
        {
            return new SambaShareServiceCheck(
                "ufw",
                "Firewall",
                true,
                PermissionIssueSeverity.Info,
                "Allowed",
                "UFW is active and appears to allow Samba or TCP 445 traffic.");
        }

        return new SambaShareServiceCheck(
            "ufw",
            "Firewall",
            false,
            PermissionIssueSeverity.Warning,
            "Review rules",
            "UFW is active, but LMS did not find an obvious Samba or TCP 445 allow rule.");
    }

    private static SambaShareRuntimeStatus BuildShareRuntimeStatus(
        SambaShareDefinition share,
        IReadOnlyList<SambaShareDefinition> liveShares)
    {
        var findings = new List<ShareSystemCheckFinding>();
        var isLiveInSamba = liveShares.Any(liveShare => SharesMatch(share, liveShare));
        var pathExists = Directory.Exists(share.SharePath);

        if (!isLiveInSamba)
        {
            findings.Add(new ShareSystemCheckFinding(
                PermissionIssueSeverity.Critical,
                "Saved in LMS only",
                "This share exists in the LMS catalog but was not found in the live Samba configuration.",
                "Add the share to `smb.conf` or another included Samba config file before expecting Windows clients to see it."));
        }

        if (!pathExists)
        {
            findings.Add(new ShareSystemCheckFinding(
                PermissionIssueSeverity.Critical,
                "Share path does not exist",
                $"The configured path `{share.SharePath}` is not present on disk.",
                "Create the directory on the LMS host or correct the path in the share definition."));
        }

        if (!share.Browseable)
        {
            findings.Add(new ShareSystemCheckFinding(
                PermissionIssueSeverity.Warning,
                "Share is hidden from browse lists",
                "The share is marked not browseable, so Windows network browse views may not list it even when the UNC path works.",
                $"Connect directly with `\\\\<server>\\{share.Name}` if the share is intentionally hidden."));
        }

        var highestSeverity = findings.Count == 0
            ? PermissionIssueSeverity.Info
            : findings.Max(finding => finding.Severity);

        return new SambaShareRuntimeStatus(
            share.Id,
            share.Name,
            share.SharePath,
            highestSeverity == PermissionIssueSeverity.Info,
            highestSeverity,
            BuildRuntimeStatusLabel(isLiveInSamba, highestSeverity),
            BuildRuntimeStatusDetail(share, isLiveInSamba, pathExists, highestSeverity),
            findings);
    }

    private static void AddCheckFinding(
        IReadOnlyList<SambaShareServiceCheck> checks,
        string key,
        ICollection<ShareSystemCheckFinding> findings,
        string title,
        string recommendation)
    {
        var check = checks.First(item => item.Key == key);
        if (check.Severity == PermissionIssueSeverity.Info)
        {
            return;
        }

        findings.Add(new ShareSystemCheckFinding(
            check.Severity,
            title,
            check.Detail,
            recommendation));
    }

    private async Task<LinuxCommandResult> RunDiagnosticCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        CancellationToken cancellationToken)
    {
        return await commandRunner.RunAsync(
            new LinuxCommandRequest(fileName, arguments, RequiresSudo: false, TimeSpan.FromSeconds(10), description)
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);
    }

    private static bool IsExecutableMissing(LinuxCommandResult result) =>
        result.ExitCode == 127 ||
        result.StandardError.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        result.StandardError.Contains("No such file", StringComparison.OrdinalIgnoreCase);

    private static bool IsSystemdUnitMissing(LinuxCommandResult result)
    {
        var text = string.Concat(result.StandardOutput, "\n", result.StandardError);
        return text.Contains("could not be found", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("not-found", StringComparison.OrdinalIgnoreCase);
    }

    private static string SummarizeCommandFailure(LinuxCommandResult result, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        var firstLine = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(firstLine) ? fallback : firstLine;
    }

    private static bool IsPort445Listening(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Contains(":445", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool FirewallAllowsSamba(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.Contains("ALLOW", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.Contains("samba", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("445", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SharesMatch(SambaShareDefinition left, SambaShareDefinition right) =>
        left.Id == right.Id ||
        (left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase) &&
         left.SharePath.Equals(right.SharePath, StringComparison.OrdinalIgnoreCase));

    private static string BuildRuntimeStatusLabel(bool isLiveInSamba, PermissionIssueSeverity severity) => severity switch
    {
        PermissionIssueSeverity.Critical when !isLiveInSamba => "Not live",
        PermissionIssueSeverity.Critical => "Needs repair",
        PermissionIssueSeverity.Warning => "Review",
        _ when isLiveInSamba => "Live",
        _ => "Catalog only"
    };

    private static string BuildRuntimeStatusDetail(
        SambaShareDefinition share,
        bool isLiveInSamba,
        bool pathExists,
        PermissionIssueSeverity severity)
    {
        if (severity == PermissionIssueSeverity.Critical && !isLiveInSamba)
        {
            return "Saved in LMS only. Windows cannot browse it until Samba exports it live.";
        }

        if (severity == PermissionIssueSeverity.Critical && !pathExists)
        {
            return "The share path is missing on disk, so Samba cannot serve it cleanly.";
        }

        if (severity == PermissionIssueSeverity.Warning && !share.Browseable)
        {
            return "The share is live, but browseable is off so Windows network lists may hide it.";
        }

        return isLiveInSamba
            ? "The share was found in live Samba configuration and its path exists on disk."
            : "The share is only stored in the LMS catalog.";
    }

    private static string BuildSystemCheckSummary(
        int shareCount,
        IReadOnlyList<SambaShareRuntimeStatus> shareStatuses,
        int criticalCount,
        int warningCount)
    {
        if (shareCount == 0)
        {
            return "No Samba shares are currently defined.";
        }

        var liveShareCount = shareStatuses.Count(status => status.Status == "Live");
        if (criticalCount == 0 && warningCount == 0)
        {
            return $"{liveShareCount} live share(s) passed the current Samba system check.";
        }

        if (criticalCount > 0)
        {
            return $"Samba needs attention: {criticalCount} critical issue(s) and {warningCount} warning(s) were found.";
        }

        return $"Samba is partly ready, but {warningCount} warning(s) still need review.";
    }

    private static SambaShareDefinition? MatchShare(IReadOnlyList<SambaShareDefinition> shares, string path) =>
        shares
            .OrderByDescending(share => share.SharePath.Length)
            .FirstOrDefault(share => path.StartsWith(share.SharePath, StringComparison.OrdinalIgnoreCase));

    private static bool HasWriteForGroup(string mask)
    {
        var normalized = NormalizeMask(mask);
        return normalized.Length >= 3 && (normalized[^2] == '2' || normalized[^2] == '3' || normalized[^2] == '6' || normalized[^2] == '7');
    }

    private static string BuildDirectoryMode(string mask)
    {
        var normalized = NormalizeMask(mask);
        var sticky = normalized.Length == 4 && normalized[0] == '1';
        var modeDigits = normalized.Length == 4 ? normalized[1..] : normalized;
        var mode = string.Concat(modeDigits.Select(MapDigitToMode));
        return $"d{mode[..3]}{mode[3..6]}{mode[6..9]}".Replace("--x", sticky ? "--T" : "--x", StringComparison.Ordinal);
    }

    private static string MapDigitToMode(char digit) => digit switch
    {
        '7' => "rwx",
        '6' => "rw-",
        '5' => "r-x",
        '4' => "r--",
        '3' => "-wx",
        '2' => "-w-",
        '1' => "--x",
        _ => "---"
    };

    private static string BuildPlainEnglishSummary(SambaShareDefinition share)
    {
        var access = share.GuestAccess ? "Anyone who reaches the share can get in." : "Access is limited to the configured users or groups.";
        var browse = share.Browseable ? "People can browse the share normally." : "Browsing is intentionally hidden.";
        var writes = share.ReadOnly ? "It is read-only by default." : "It is intended to support writes.";
        return $"{access} {browse} {writes} New files use mask {share.CreateMask} and new folders use mask {share.DirectoryMask}.";
    }

    private static IReadOnlyList<string> BuildConflicts(SambaShareDefinition share)
    {
        var conflicts = new List<string>();

        if (share.GuestAccess && (share.ValidUsers.Count > 0 || share.ValidGroups.Count > 0))
        {
            conflicts.Add("Guest access and explicit membership rules are both configured, which sends mixed signals.");
        }

        if (share.ReadOnly && share.WriteList.Count > 0)
        {
            conflicts.Add("The share is marked read-only but still has a write list, so operators may assume writes are possible when they are not.");
        }

        if (!share.ReadOnly && !HasWriteForGroup(share.CreateMask) && share.WriteList.Count == 0)
        {
            conflicts.Add("The share says write is allowed, but the file mask does not support collaborative edits.");
        }

        if (string.IsNullOrWhiteSpace(share.ForceGroup) && share.ValidGroups.Count > 0)
        {
            conflicts.Add("No forced group is configured, so new content may drift into inconsistent ownership.");
        }

        if (conflicts.Count == 0)
        {
            conflicts.Add("No obvious Samba-versus-policy conflict was detected from the saved share definition.");
        }

        return conflicts;
    }

    private static bool MatchesPrincipalOrGroup(IReadOnlyList<string> entries, string principal, IReadOnlyList<string> groups) =>
        entries.Any(entry =>
            entry.Equals(principal, StringComparison.OrdinalIgnoreCase) ||
            entry.StartsWith("@", StringComparison.Ordinal) &&
            groups.Contains(entry[1..], StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyList<string> BuildAccessReasons(
        SambaShareDefinition share,
        string principal,
        IReadOnlyList<string> groups,
        bool canAccess,
        bool canCreate,
        bool canDeleteOthers,
        bool hasReadOverride,
        bool hasWriteOverride)
    {
        var reasons = new List<string>();

        reasons.Add(canAccess
            ? $"`{principal}` matches the configured share policy."
            : $"`{principal}` does not match the configured valid users, groups, or guest policy.");

        if (groups.Count > 0)
        {
            reasons.Add($"Evaluated group membership: {string.Join(", ", groups)}.");
        }

        if (hasReadOverride)
        {
            reasons.Add("A read-list entry explicitly grants read access.");
        }

        if (hasWriteOverride)
        {
            reasons.Add("A write-list entry explicitly grants write access.");
        }

        reasons.Add(canCreate
            ? "The current share policy allows file creation."
            : share.ReadOnly
                ? "File creation is blocked because the share is marked read-only."
                : "File creation is blocked because no write-capable rule matched.");

        reasons.Add(canDeleteOthers
            ? "Deleting files created by other users is not explicitly constrained by the current directory mask."
            : "Deleting files created by others is limited by the current policy or sticky-style directory behavior.");

        return reasons;
    }

    private async Task SyncCurrentSharesAsync(CancellationToken cancellationToken)
    {
        var discoveredShares = await sambaConfigurationShareReader.ListSharesAsync(cancellationToken);
        if (discoveredShares.Count == 0)
        {
            return;
        }

        var existingEntities = await dbContext.SambaShares.ToListAsync(cancellationToken);
        var hasChanges = false;

        foreach (var discoveredShare in discoveredShares)
        {
            var existingEntity = FindMatchingShare(existingEntities, discoveredShare);
            if (existingEntity is null)
            {
                var newEntity = Map(discoveredShare);
                dbContext.SambaShares.Add(newEntity);
                existingEntities.Add(newEntity);
                hasChanges = true;
                continue;
            }

            hasChanges |= ApplyDiscoveredShare(existingEntity, discoveredShare);
        }

        if (hasChanges)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<bool> IsLiveSambaShareAsync(
        SambaShareDefinition share,
        CancellationToken cancellationToken)
    {
        var discoveredShares = await sambaConfigurationShareReader.ListSharesAsync(cancellationToken);
        return discoveredShares.Any(discovered =>
            discovered.Id == share.Id ||
            discovered.Name.Equals(share.Name, StringComparison.OrdinalIgnoreCase) &&
            discovered.SharePath.Equals(share.SharePath, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<SystemUserEntry>> ReadSystemUserEntriesAsync(CancellationToken cancellationToken)
    {
        var lines = await ReadMachineLinesAsync(
            "getent",
            ["passwd"],
            "/etc/passwd",
            "Read Linux users",
            cancellationToken);

        return lines
            .Select(ParseUserEntry)
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .ToArray();
    }

    private async Task<IReadOnlyList<SystemGroupEntry>> ReadSystemGroupEntriesAsync(CancellationToken cancellationToken)
    {
        var lines = await ReadMachineLinesAsync(
            "getent",
            ["group"],
            "/etc/group",
            "Read Linux groups",
            cancellationToken);

        return lines
            .Select(ParseGroupEntry)
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .ToArray();
    }

    private async Task<IReadOnlyList<string>> ReadMachineLinesAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string fallbackPath,
        string description,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(fileName, arguments, RequiresSudo: false, Timeout: TimeSpan.FromSeconds(10), description),
            dryRun: false,
            cancellationToken);

        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (File.Exists(fallbackPath))
        {
            return await File.ReadAllLinesAsync(fallbackPath, cancellationToken);
        }

        return Array.Empty<string>();
    }

    private async Task EnsureGroupExistsAsync(string groupName, CancellationToken cancellationToken)
    {
        var groups = await ListGroupsAsync(cancellationToken);
        if (groups.Any(group => group.GroupName.Equals(groupName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        await RunRequiredCommandAsync(
            "groupadd",
            [groupName],
            $"Create Linux group {groupName}",
            requiresSudo: true,
            cancellationToken);
    }

    private async Task RunRequiredCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        bool requiresSudo,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(fileName, arguments, requiresSudo, TimeSpan.FromSeconds(30), description),
            dryRun: false,
            cancellationToken);

        if (result.ExitCode == 0)
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? $"{description} failed with exit code {result.ExitCode}."
            : $"{description} failed: {message}");
    }

    private static SystemUserEntry? ParseUserEntry(string line)
    {
        var parts = line.Split(':');
        if (parts.Length < 7 || !int.TryParse(parts[2], out var uid) || !int.TryParse(parts[3], out var gid))
        {
            return null;
        }

        var displayName = parts[4].Split(',', 2, StringSplitOptions.TrimEntries)[0];
        return new SystemUserEntry(parts[0], uid, gid, displayName, parts[5], parts[6]);
    }

    private static SystemGroupEntry? ParseGroupEntry(string line)
    {
        var parts = line.Split(':');
        if (parts.Length < 4 || !int.TryParse(parts[2], out var gid))
        {
            return null;
        }

        var members = parts[3]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new SystemGroupEntry(parts[0], gid, members);
    }

    private static Guid CreateStableId(string scope, string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}:{value.Trim().ToLowerInvariant()}"));
        return new Guid(bytes[..16]);
    }

    private static bool IsNonInteractiveShell(string shell) =>
        shell.EndsWith("/nologin", StringComparison.OrdinalIgnoreCase) ||
        shell.EndsWith("/false", StringComparison.OrdinalIgnoreCase);

    private static bool IsLikelyHumanUser(LinuxShareUser user) =>
        user.HomeDirectory.StartsWith("/home/", StringComparison.OrdinalIgnoreCase) &&
        !IsNonInteractiveShell(user.LoginShell);

    private static SambaShareDefinition Map(SambaShareEntity entity) =>
        new(
            entity.Id,
            entity.Name,
            entity.SharePath,
            entity.Description,
            entity.Browseable,
            entity.ReadOnly,
            entity.GuestAccess,
            DeserializeList(entity.ValidUsersJson),
            DeserializeList(entity.ValidGroupsJson),
            DeserializeList(entity.WriteListJson),
            DeserializeList(entity.ReadListJson),
            entity.ForceUser,
            entity.ForceGroup,
            entity.CreateMask,
            entity.DirectoryMask,
            entity.CreateMaskExplanation,
            entity.DirectoryMaskExplanation);

    private static SambaShareEntity Map(SambaShareDefinition share) =>
        new()
        {
            Id = share.Id,
            Name = share.Name,
            SharePath = share.SharePath,
            Description = share.Description,
            Browseable = share.Browseable,
            ReadOnly = share.ReadOnly,
            GuestAccess = share.GuestAccess,
            ValidUsersJson = SerializeList(share.ValidUsers),
            ValidGroupsJson = SerializeList(share.ValidGroups),
            WriteListJson = SerializeList(share.WriteList),
            ReadListJson = SerializeList(share.ReadList),
            ForceUser = share.ForceUser,
            ForceGroup = share.ForceGroup,
            CreateMask = share.CreateMask,
            DirectoryMask = share.DirectoryMask,
            CreateMaskExplanation = share.CreateMaskExplanation,
            DirectoryMaskExplanation = share.DirectoryMaskExplanation
        };

    private static LinuxShareUser Map(LinuxShareUserEntity entity) =>
        new(
            entity.Id,
            entity.UserName,
            -1,
            -1,
            entity.DisplayName,
            entity.PrimaryGroup,
            DeserializeList(entity.SupplementaryGroupsJson),
            entity.HomeDirectory,
            entity.LoginShell,
            entity.IsEnabled);

    private static LinuxShareUserEntity Map(LinuxShareUser user) =>
        new()
        {
            Id = user.Id,
            UserName = user.UserName,
            DisplayName = user.DisplayName,
            PrimaryGroup = user.PrimaryGroup,
            SupplementaryGroupsJson = SerializeList(user.SupplementaryGroups),
            HomeDirectory = user.HomeDirectory,
            LoginShell = user.LoginShell,
            IsEnabled = user.IsEnabled
        };

    private static LinuxShareGroup Map(LinuxShareGroupEntity entity) =>
        new(
            entity.Id,
            entity.GroupName,
            -1,
            entity.Description,
            DeserializeList(entity.MembersJson));

    private static LinuxShareGroupEntity Map(LinuxShareGroup group) =>
        new()
        {
            Id = group.Id,
            GroupName = group.GroupName,
            Description = group.Description,
            MembersJson = SerializeList(group.Members)
        };

    private static LocalUserAccessPolicy Map(LocalUserAccessPolicyEntity entity) =>
        new(
            entity.UserName,
            entity.IsManagedPolicy,
            (RemoteAccessSshAuthenticationMode)entity.SshAuthenticationMode,
            entity.AuthorizedKeyEntries,
            entity.UpdatedAtUtc,
            entity.PasswordChangedAtUtc);

    private static string SerializeList(IReadOnlyList<string> values) => JsonSerializer.Serialize(values);

    private static IReadOnlyList<string> DeserializeList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return JsonSerializer.Deserialize<string[]>(value) ?? Array.Empty<string>();
    }

    private static string NormalizeMask(string mask)
    {
        var trimmed = mask.Trim();
        return trimmed.Length switch
        {
            3 => trimmed,
            4 => trimmed,
            _ => "2775"
        };
    }

    private async Task ApplyUserAccessPoliciesAsync(CancellationToken cancellationToken)
    {
        var policies = await ListUserAccessPoliciesAsync(cancellationToken);
        await localUserAccessSystemService.ApplyPoliciesAsync(
            policies.Where(policy => policy.IsManagedPolicy).ToArray(),
            cancellationToken);
    }

    private sealed record SystemUserEntry(
        string UserName,
        int Uid,
        int Gid,
        string DisplayName,
        string HomeDirectory,
        string LoginShell);

    private sealed record SystemGroupEntry(
        string GroupName,
        int Gid,
        IReadOnlyList<string> Members);

    private static SambaShareEntity? FindMatchingShare(
        IReadOnlyList<SambaShareEntity> existingEntities,
        SambaShareDefinition discoveredShare)
    {
        var normalizedName = NormalizeMatchValue(discoveredShare.Name);
        var normalizedPath = NormalizeMatchValue(discoveredShare.SharePath);

        return existingEntities.FirstOrDefault(entity => entity.Id == discoveredShare.Id) ??
               existingEntities.FirstOrDefault(entity =>
                   NormalizeMatchValue(entity.Name) == normalizedName &&
                   NormalizeMatchValue(entity.SharePath) == normalizedPath) ??
               FindUniqueMatch(existingEntities, normalizedName, entity => entity.Name) ??
               FindUniqueMatch(existingEntities, normalizedPath, entity => entity.SharePath);
    }

    private static SambaShareEntity? FindUniqueMatch(
        IReadOnlyList<SambaShareEntity> existingEntities,
        string value,
        Func<SambaShareEntity, string> selector)
    {
        var matches = existingEntities
            .Where(entity => NormalizeMatchValue(selector(entity)) == value)
            .Take(2)
            .ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }

    private static string NormalizeMatchValue(string value) =>
        value.Trim().ToLowerInvariant();

    private static bool ApplyDiscoveredShare(SambaShareEntity entity, SambaShareDefinition discoveredShare)
    {
        var hasChanges = false;

        hasChanges |= SetIfChanged(entity.Name, discoveredShare.Name, value => entity.Name = value);
        hasChanges |= SetIfChanged(entity.SharePath, discoveredShare.SharePath, value => entity.SharePath = value);
        hasChanges |= SetIfChanged(entity.Description, discoveredShare.Description, value => entity.Description = value);
        hasChanges |= SetIfChanged(entity.Browseable, discoveredShare.Browseable, value => entity.Browseable = value);
        hasChanges |= SetIfChanged(entity.ReadOnly, discoveredShare.ReadOnly, value => entity.ReadOnly = value);
        hasChanges |= SetIfChanged(entity.GuestAccess, discoveredShare.GuestAccess, value => entity.GuestAccess = value);
        hasChanges |= SetIfChanged(entity.ValidUsersJson, SerializeList(discoveredShare.ValidUsers), value => entity.ValidUsersJson = value);
        hasChanges |= SetIfChanged(entity.ValidGroupsJson, SerializeList(discoveredShare.ValidGroups), value => entity.ValidGroupsJson = value);
        hasChanges |= SetIfChanged(entity.WriteListJson, SerializeList(discoveredShare.WriteList), value => entity.WriteListJson = value);
        hasChanges |= SetIfChanged(entity.ReadListJson, SerializeList(discoveredShare.ReadList), value => entity.ReadListJson = value);
        hasChanges |= SetIfChanged(entity.ForceUser, discoveredShare.ForceUser, value => entity.ForceUser = value);
        hasChanges |= SetIfChanged(entity.ForceGroup, discoveredShare.ForceGroup, value => entity.ForceGroup = value);
        hasChanges |= SetIfChanged(entity.CreateMask, discoveredShare.CreateMask, value => entity.CreateMask = value);
        hasChanges |= SetIfChanged(entity.DirectoryMask, discoveredShare.DirectoryMask, value => entity.DirectoryMask = value);
        hasChanges |= SetIfChanged(entity.CreateMaskExplanation, discoveredShare.CreateMaskExplanation, value => entity.CreateMaskExplanation = value);
        hasChanges |= SetIfChanged(entity.DirectoryMaskExplanation, discoveredShare.DirectoryMaskExplanation, value => entity.DirectoryMaskExplanation = value);

        return hasChanges;
    }

    private static bool SetIfChanged<T>(T currentValue, T newValue, Action<T> apply)
    {
        if (EqualityComparer<T>.Default.Equals(currentValue, newValue))
        {
            return false;
        }

        apply(newValue);
        return true;
    }
}
