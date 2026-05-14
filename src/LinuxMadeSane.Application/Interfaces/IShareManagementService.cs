using LinuxMadeSane.Application.Contracts.Shares;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Application.Interfaces;

public interface IShareManagementService
{
    Task<SharesDashboardViewModel> GetDashboardAsync(CancellationToken cancellationToken = default);

    Task<ShareManagerViewModel> GetShareManagerAsync(CancellationToken cancellationToken = default);

    Task<UsersGroupsManagerViewModel> GetUsersGroupsManagerAsync(CancellationToken cancellationToken = default);

    Task<ShareEditor> GetEditorAsync(Guid? id, CancellationToken cancellationToken = default);

    Task<Guid> SaveShareAsync(ShareEditor editor, CancellationToken cancellationToken = default);

    Task RepairShareAsync(Guid id, CancellationToken cancellationToken = default);

    Task DeleteShareAsync(Guid id, CancellationToken cancellationToken = default);

    Task<UserEditor> GetUserEditorAsync(Guid? id, CancellationToken cancellationToken = default);

    Task<Guid> SaveUserAsync(UserEditor editor, CancellationToken cancellationToken = default);

    Task DeleteUserAsync(Guid id, CancellationToken cancellationToken = default);

    Task<LocalUserAccessEditor> GetLocalUserAccessEditorAsync(Guid userId, CancellationToken cancellationToken = default);

    Task SaveLocalUserAccessAsync(Guid userId, LocalUserAccessEditor editor, CancellationToken cancellationToken = default);

    Task<LocalUserPasswordResetViewModel> BuildLocalUserPasswordResetAsync(Guid userId, CancellationToken cancellationToken = default);

    Task ResetLocalUserPasswordAsync(Guid userId, string newPassword, CancellationToken cancellationToken = default);

    Task<GroupEditor> GetGroupEditorAsync(Guid? id, CancellationToken cancellationToken = default);

    Task<Guid> SaveGroupAsync(GroupEditor editor, CancellationToken cancellationToken = default);

    Task DeleteGroupAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PermissionExplainerViewModel> GetPermissionExplainerAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<EffectiveAccessCheckerViewModel> GetEffectiveAccessCheckerAsync(
        Guid shareId,
        string principal,
        IReadOnlyList<string> groupMembership,
        CancellationToken cancellationToken = default);

    Task<OwnershipWizardViewModel> GetWizardAsync(CancellationToken cancellationToken = default);

    Task<PermissionFixViewModel> GetFixPlanAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<MountHelperViewModel?> GetMountHelperAsync(
        Guid shareId,
        CancellationToken cancellationToken = default);

    Task<NetworkShareMountExplorerViewModel> GetNetworkShareMountExplorerAsync(
        NetworkShareDiscoveryScope discoveryScope = NetworkShareDiscoveryScope.Lan,
        CancellationToken cancellationToken = default);

    Task<SshfsMountExplorerViewModel> GetSshfsMountExplorerAsync(CancellationToken cancellationToken = default);

    Task<RemoteShareBrowseResult> BrowseRemoteSharesAsync(
        RemoteShareConnectionEditor editor,
        CancellationToken cancellationToken = default);

    Task<RemoteShareMountResult> CreateRemoteShareMountAsync(
        RemoteShareMountEditor editor,
        CancellationToken cancellationToken = default);

    Task<RemoteShareMountResult> UpdateManagedRemoteMountAsync(
        Guid id,
        RemoteShareMountEditor editor,
        CancellationToken cancellationToken = default);

    Task<SshfsMountResult> CreateSshfsMountAsync(
        SshfsMountEditor editor,
        CancellationToken cancellationToken = default);

    Task<ShareToolingInstallResult> InstallMissingShareToolingAsync(CancellationToken cancellationToken = default);

    Task<ShareToolingInstallResult> InstallMissingSshfsToolingAsync(CancellationToken cancellationToken = default);

    Task<SambaSystemActionResult> RunSambaSystemActionAsync(
        SambaSystemAction action,
        CancellationToken cancellationToken = default);

    Task CleanupTemporaryRemoteMountsAsync(CancellationToken cancellationToken = default);

    Task<bool> ReleaseTemporaryRemoteMountAsync(string localMountPath, CancellationToken cancellationToken = default);

    Task<bool> DisconnectCurrentRemoteMountAsync(string localMountPath, CancellationToken cancellationToken = default);

    Task DeleteManagedRemoteMountAsync(Guid id, CancellationToken cancellationToken = default);

    Task DeleteManagedSshfsMountAsync(Guid id, CancellationToken cancellationToken = default);
}
