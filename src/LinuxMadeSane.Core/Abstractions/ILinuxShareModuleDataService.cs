// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILinuxShareModuleDataService
{
    Task<IReadOnlyList<SambaShareDefinition>> ListSharesAsync(CancellationToken cancellationToken = default);

    Task<SambaShareDefinition?> GetShareAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveShareAsync(SambaShareDefinition share, CancellationToken cancellationToken = default);

    Task DeleteShareAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LinuxShareUser>> ListUsersAsync(CancellationToken cancellationToken = default);

    Task<LinuxShareUser?> GetUserAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveUserAsync(LinuxShareUser user, CancellationToken cancellationToken = default);

    Task DeleteUserAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalUserAccessPolicy>> ListUserAccessPoliciesAsync(CancellationToken cancellationToken = default);

    Task<LocalUserAccessPolicy?> GetUserAccessPolicyAsync(string userName, CancellationToken cancellationToken = default);

    Task SaveUserAccessPolicyAsync(LocalUserAccessPolicy policy, CancellationToken cancellationToken = default);

    Task ResetUserPasswordAsync(string userName, string newPassword, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LinuxShareGroup>> ListGroupsAsync(CancellationToken cancellationToken = default);

    Task<LinuxShareGroup?> GetGroupAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveGroupAsync(LinuxShareGroup group, CancellationToken cancellationToken = default);

    Task DeleteGroupAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PermissionExplainerResult> ExplainPermissionsAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<EffectiveAccessCheckResult> CheckEffectiveAccessAsync(
        Guid shareId,
        string principal,
        IReadOnlyList<string> groupMembership,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnershipWizardPreset>> ListPresetsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PermissionIssue>> ScanIssuesAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MountRecipe>> GetMountRecipesAsync(
        Guid shareId,
        CancellationToken cancellationToken = default);

    Task<SambaShareSystemCheckResult> GetSambaSystemCheckAsync(
        CancellationToken cancellationToken = default);

    Task<NetworkShareMachineDiscoveryResult> DiscoverNetworkShareMachinesAsync(
        NetworkShareDiscoveryScope scope = NetworkShareDiscoveryScope.Lan,
        CancellationToken cancellationToken = default);

    Task<RemoteShareBrowseResult> BrowseRemoteSharesAsync(
        string target,
        string? userName,
        string? password,
        string? domain,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ManagedRemoteShareMount>> ListManagedRemoteMountsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CurrentSystemMount>> ListCurrentMountsAsync(
        CancellationToken cancellationToken = default);

    Task<RemoteShareMountResult> CreateRemoteShareMountAsync(
        RemoteShareMountRequest request,
        CancellationToken cancellationToken = default);

    Task<RemoteShareMountResult> UpdateManagedRemoteMountAsync(
        Guid id,
        RemoteShareMountRequest request,
        bool keepSavedPassword,
        CancellationToken cancellationToken = default);

    Task<RemoteShareMountResult?> ReconnectManagedRemoteMountAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<RemoteShareMountResult?>(null);

    Task CleanupTemporaryRemoteMountsAsync(CancellationToken cancellationToken = default);

    Task<bool> ReleaseTemporaryRemoteMountAsync(string localMountPath, CancellationToken cancellationToken = default);

    Task<bool> DisconnectCurrentRemoteMountAsync(string localMountPath, CancellationToken cancellationToken = default);

    Task DeleteManagedRemoteMountAsync(Guid id, CancellationToken cancellationToken = default);
}
