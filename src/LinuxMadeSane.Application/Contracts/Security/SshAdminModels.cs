// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record SshAdminOverview(
    bool IsOpenSshInstalled,
    string OpenSshVersion,
    bool IsServiceActive,
    string ServiceName,
    bool IsConfigurationValid,
    string ConfigurationValidationMessage,
    string HostName,
    IReadOnlyList<string> HostAddresses,
    SshEffectiveSettings EffectiveSettings,
    IReadOnlyList<SshHostKeyViewModel> HostKeys,
    IReadOnlyList<SshLocalUserViewModel> Users,
    IReadOnlyList<SshSettingAssessment> Assessments,
    bool IsManagedByLms,
    string ManagedConfigurationPath,
    bool IsAuthenticatorModuleInstalled,
    bool CanInstallAuthenticatorModule,
    SshAdminTrialViewModel? ActiveTrial);

public sealed record SshEffectiveSettings(
    IReadOnlyList<int> Ports,
    IReadOnlyList<string> ListenAddresses,
    string PermitRootLogin,
    bool PasswordAuthentication,
    bool PublicKeyAuthentication,
    bool KeyboardInteractiveAuthentication,
    bool UsePam,
    bool PermitEmptyPasswords,
    bool X11Forwarding,
    bool AllowAgentForwarding,
    bool AllowTcpForwarding,
    int MaxAuthTries,
    int LoginGraceTimeSeconds);

public sealed record SshHostKeyViewModel(
    string Algorithm,
    string FingerprintSha256,
    int Bits);

public sealed record SshLocalUserViewModel(
    string UserName,
    string DisplayName,
    string HomeDirectory,
    string Shell,
    bool IsLocked,
    int AuthorizedKeyCount,
    IReadOnlyList<SshAuthorizedKeyViewModel> AuthorizedKeys,
    bool IsPublicKeyAuthenticationEnabled,
    string AuthenticationMethods,
    bool HasAuthenticator,
    bool IsAuthenticatorRequired);

public sealed record SshAuthorizedKeyViewModel(
    string Algorithm,
    string FingerprintSha256,
    string Comment);

public sealed record SshSettingAssessment(
    string Label,
    string Value,
    string Explanation,
    SshAssessmentTone Tone);

public enum SshAssessmentTone
{
    Neutral = 0,
    Good = 1,
    Attention = 2
}

public sealed class SshHardeningEditor
{
    [Range(1, 65535)]
    public int Port { get; set; } = 22;

    public bool PermitRootLogin { get; set; }

    public bool PasswordAuthentication { get; set; } = true;

    public bool PublicKeyAuthentication { get; set; } = true;

    public bool KeyboardInteractiveAuthentication { get; set; }

    public bool UsePam { get; set; } = true;

    public bool X11Forwarding { get; set; }

    public bool AllowAgentForwarding { get; set; }

    public bool AllowTcpForwarding { get; set; } = true;

    [Range(1, 10)]
    public int MaxAuthTries { get; set; } = 3;

    [Range(10, 120)]
    public int LoginGraceTimeSeconds { get; set; } = 30;
}

public sealed record SshHardeningPlan(
    string GeneratedConfiguration,
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> SafetyChecks,
    IReadOnlyList<string> Warnings,
    bool RequiresVerifiedKeyLogin,
    bool CanApply);

public sealed record SshAdminTrialViewModel(
    Guid Id,
    string Title,
    string Detail,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record SshAdminOperationResult(
    bool Succeeded,
    string Message,
    SshAdminTrialViewModel? Trial = null);

public sealed record SshPublicKeyInstallResult(
    bool Succeeded,
    string Message,
    string FingerprintSha256,
    bool WasAlreadyInstalled);

public sealed record SshKeyLoginVerificationResult(
    bool Succeeded,
    string Message,
    DateTimeOffset? AcceptedAtUtc = null);

public sealed record SshTotpEnrollment(
    string UserName,
    string Secret,
    string ManualEntryKey,
    string OtpUri,
    IReadOnlyList<string> RecoveryCodes);
