using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISftpAuthPolicyService
{
    IReadOnlyList<string> GetSupplementaryGroups(SftpAuthenticationMode mode);

    string NormalizePublicKey(string publicKeyText);

    string BuildAuthorizedKeysFileContents(IReadOnlyList<string> publicKeys);

    void ValidatePublicKey(string publicKeyText);

    string BuildFingerprint(string publicKeyText);
}
