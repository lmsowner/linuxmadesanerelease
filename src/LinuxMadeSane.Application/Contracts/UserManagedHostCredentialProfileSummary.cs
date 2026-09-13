// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts;

public sealed record UserManagedHostCredentialProfileSummary(
    Guid Id,
    string Name,
    string Username,
    bool HasStoredPassword,
    bool HasStoredPrivateKey,
    bool HasStoredPrivateKeyPassphrase,
    DateTimeOffset UpdatedAtUtc);
