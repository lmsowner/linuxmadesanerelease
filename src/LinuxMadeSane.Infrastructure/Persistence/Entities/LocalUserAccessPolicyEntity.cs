namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class LocalUserAccessPolicyEntity
{
    public string UserName { get; set; } = string.Empty;

    public bool IsManagedPolicy { get; set; }

    public int SshAuthenticationMode { get; set; }

    public string AuthorizedKeyEntries { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? PasswordChangedAtUtc { get; set; }
}
