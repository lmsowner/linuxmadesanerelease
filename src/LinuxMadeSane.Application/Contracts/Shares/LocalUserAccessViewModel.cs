using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record LocalUserAccessViewModel(
    string UserName,
    bool HasManagedPolicy,
    RemoteAccessSshAuthenticationMode? SshAuthenticationMode,
    bool HasAuthorizedKeys,
    DateTimeOffset? PasswordChangedAtUtc);
