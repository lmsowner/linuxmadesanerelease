// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Security;

namespace LinuxMadeSane.Application.Interfaces;

public interface ISecuritySettingsService
{
    Task<SecuritySettingsPageViewModel> GetPageAsync(CancellationToken cancellationToken = default);
    Task<InitialSetupViewModel> GetInitialSetupAsync(CancellationToken cancellationToken = default);
    Task<SecurityUserProvisioningViewModel?> GetInitialSetupProvisioningAsync(string? lmsLoginUrl = null, CancellationToken cancellationToken = default);
    Task<SecurityUserProvisioningViewModel> StartInitialSetupAsync(SecurityUserEditor editor, string? lmsLoginUrl = null, CancellationToken cancellationToken = default);
    Task<SecurityUserProvisioningViewModel> ResetInitialSetupOtpAsync(string? lmsLoginUrl = null, CancellationToken cancellationToken = default);
    Task<SecurityAuthenticationResult> ConfirmInitialSetupOtpAsync(Guid userId, string otpCode, CancellationToken cancellationToken = default);
    Task<SecurityUserProvisioningViewModel> CreateUserAsync(SecurityUserEditor editor, string? lmsLoginUrl = null, CancellationToken cancellationToken = default);
    Task<SecurityUserAccessEditor> GetUserEditorAsync(Guid userId, CancellationToken cancellationToken = default);
    Task SaveUserAsync(SecurityUserAccessEditor editor, CancellationToken cancellationToken = default);
    Task<SecurityUserProvisioningViewModel> ResetUserOtpAsync(Guid userId, string? lmsLoginUrl = null, CancellationToken cancellationToken = default);
    Task<SecurityUserPasswordResetViewModel> BuildPasswordResetAsync(Guid userId, CancellationToken cancellationToken = default);
    Task ResetUserPasswordAsync(Guid userId, string newPassword, CancellationToken cancellationToken = default);
    Task SetUserEnabledAsync(Guid userId, bool isEnabled, CancellationToken cancellationToken = default);
    Task SetUserSessionLifetimeAsync(Guid userId, int sessionLifetimeMinutes, CancellationToken cancellationToken = default);
    Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<SecurityMessagingSettingsEditor> GetMessagingEditorAsync(CancellationToken cancellationToken = default);
    Task SaveMessagingSettingsAsync(SecurityMessagingSettingsEditor editor, CancellationToken cancellationToken = default);
    Task<SecurityMessagingTestResult> SendMessagingTestAsync(string recipientAddress, CancellationToken cancellationToken = default);
    Task<Guid> SaveTrustedNetworkAsync(TrustedNetworkEntryEditor editor, CancellationToken cancellationToken = default);
    Task SetTrustedNetworkEnabledAsync(Guid entryId, bool isEnabled, CancellationToken cancellationToken = default);
    Task SetTrustedNetworkTrustedAccessEnabledAsync(Guid entryId, bool isEnabled, CancellationToken cancellationToken = default);
    Task SetTrustedNetworkAuthenticationEnabledAsync(Guid entryId, bool isEnabled, CancellationToken cancellationToken = default);
    Task DeleteTrustedNetworkAsync(Guid entryId, CancellationToken cancellationToken = default);
}
