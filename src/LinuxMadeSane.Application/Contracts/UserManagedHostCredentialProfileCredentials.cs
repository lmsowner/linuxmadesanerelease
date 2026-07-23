// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts;

public sealed record UserManagedHostCredentialProfileCredentials(
    Guid Id,
    string Name,
    string Username,
    string Password,
    string PrivateKey,
    string PrivateKeyPassphrase);
