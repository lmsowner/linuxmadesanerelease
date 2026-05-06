using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record SshCredentialType(
    AuthenticationType AuthenticationType,
    string DisplayName,
    string Description,
    bool RequiresSecretReference);
