using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record LocalUserAccessPolicy(
    string UserName,
    bool IsManagedPolicy,
    RemoteAccessSshAuthenticationMode SshAuthenticationMode,
    string AuthorizedKeyEntries,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? PasswordChangedAtUtc);
