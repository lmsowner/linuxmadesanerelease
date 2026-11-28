// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
