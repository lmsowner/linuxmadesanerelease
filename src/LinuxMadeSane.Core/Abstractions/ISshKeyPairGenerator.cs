// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
