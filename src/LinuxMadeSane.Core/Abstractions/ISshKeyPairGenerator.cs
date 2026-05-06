using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISshKeyPairGenerator
{
    Task<GeneratedSshKeyPair> GenerateAsync(
        SshKeyGenerationProfile profile = SshKeyGenerationProfile.Ed25519,
        string? comment = null,
        CancellationToken cancellationToken = default);
}
