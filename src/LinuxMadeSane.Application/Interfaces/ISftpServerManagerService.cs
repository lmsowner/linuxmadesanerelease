using LinuxMadeSane.Application.Contracts.Sftp;
using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Application.Interfaces;

public interface ISftpServerManagerService
{
    Task<SftpServerWorkspaceViewModel> GetWorkspaceAsync(CancellationToken cancellationToken = default);

    Task<SftpHostSettingsEditor> GetHostSettingsEditorAsync(CancellationToken cancellationToken = default);

    Task<SftpConfigurationPlan> PreviewHostConfigurationAsync(
        SftpHostSettingsEditor editor,
        CancellationToken cancellationToken = default);

    Task<SftpApplyResult> ApplyHostConfigurationAsync(
        SftpHostSettingsEditor editor,
        bool approved,
        CancellationToken cancellationToken = default);

    Task<SftpManagedUserEditor> GetUserEditorAsync(
        string? userName,
        CancellationToken cancellationToken = default);

    Task<SftpConfigurationPlan> PreviewUserAsync(
        SftpManagedUserEditor editor,
        string? password,
        CancellationToken cancellationToken = default);

    Task<SftpApplyResult> SaveUserAsync(
        SftpManagedUserEditor editor,
        string? password,
        bool approved,
        CancellationToken cancellationToken = default);

    Task<SftpUserDetailsViewModel?> GetUserDetailsAsync(
        string userName,
        CancellationToken cancellationToken = default);

    Task<SftpPasswordResetEditor> GetPasswordResetEditorAsync(
        string userName,
        CancellationToken cancellationToken = default);

    Task<SftpConfigurationPlan> PreviewPasswordResetAsync(
        SftpPasswordResetEditor editor,
        CancellationToken cancellationToken = default);

    Task<SftpApplyResult> ResetPasswordAsync(
        SftpPasswordResetEditor editor,
        bool approved,
        CancellationToken cancellationToken = default);

    Task<SftpConfigurationPlan> PreviewDisableUserAsync(
        string userName,
        CancellationToken cancellationToken = default);

    Task<SftpApplyResult> DisableUserAsync(
        string userName,
        bool approved,
        CancellationToken cancellationToken = default);

    Task<SftpConfigurationPlan> PreviewDeleteUserAsync(
        string userName,
        CancellationToken cancellationToken = default);

    Task<SftpApplyResult> DeleteUserAsync(
        string userName,
        bool approved,
        CancellationToken cancellationToken = default);
}
