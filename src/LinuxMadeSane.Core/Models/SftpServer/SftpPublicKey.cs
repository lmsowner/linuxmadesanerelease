// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpPublicKey(
    Guid Id,
    string Label,
    string KeyType,
    string Fingerprint,
    string PublicKeyText,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
