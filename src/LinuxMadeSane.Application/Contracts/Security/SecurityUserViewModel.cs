using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record SecurityUserViewModel(
    Guid Id,
    string Email,
    string LinuxUsername,
    bool IsEnabled,
    int SessionLifetimeMinutes,
    RemoteAccessSshAuthenticationMode SshAuthenticationMode,
    bool HasAuthorizedKeys,
    bool IsLocalAccountManaged,
    bool HasOtpSecret,
    DateTimeOffset? LastLoginAtUtc,
    DateTimeOffset? PasswordChangedAtUtc);
