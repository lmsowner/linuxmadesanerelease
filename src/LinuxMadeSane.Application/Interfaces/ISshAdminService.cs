// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Security;

namespace LinuxMadeSane.Application.Interfaces;

public interface ISshAdminService
{
    Task<SshAdminOverview> GetOverviewAsync(CancellationToken cancellationToken = default);

    Task<SshHardeningPlan> PreviewHardeningAsync(
        SshHardeningEditor editor,
        string? verifiedUserName,
        CancellationToken cancellationToken = default);

    Task<SshAdminOperationResult> StartHardeningTrialAsync(
        SshHardeningEditor editor,
        string? verifiedUserName,
        CancellationToken cancellationToken = default);

    Task ConfirmTrialAsync(Guid trialId, CancellationToken cancellationToken = default);

    Task RevertTrialAsync(Guid trialId, CancellationToken cancellationToken = default);

    Task<SshPublicKeyInstallResult> InstallPublicKeyAsync(
        string userName,
        string publicKey,
        CancellationToken cancellationToken = default);

    Task<SshAdminOperationResult> RemovePublicKeyAsync(
        string userName,
        string fingerprintSha256,
        CancellationToken cancellationToken = default);

    Task<SshKeyLoginVerificationResult> VerifyRecentPublicKeyLoginAsync(
        string userName,
        string fingerprintSha256,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default);

    Task<SshTotpEnrollment> BeginAuthenticatorEnrollmentAsync(
        string userName,
        CancellationToken cancellationToken = default);

    Task<SshAdminOperationResult> EnableAuthenticatorAsync(
        string userName,
        string secret,
        string verificationCode,
        IReadOnlyList<string> recoveryCodes,
        string fingerprintSha256,
        DateTimeOffset keyLoginVerifiedAtUtc,
        CancellationToken cancellationToken = default);

    Task<SshAdminOperationResult> DisableAuthenticatorAsync(
        string userName,
        CancellationToken cancellationToken = default);
}
