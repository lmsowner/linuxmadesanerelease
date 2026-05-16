// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record GeneratedSshKeyPair(
    string Algorithm,
    string PrivateKey,
    string PublicKey,
    string FingerprintSha256);
