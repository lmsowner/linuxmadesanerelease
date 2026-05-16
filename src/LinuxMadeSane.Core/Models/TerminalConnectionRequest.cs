// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record TerminalConnectionRequest(
    string Username,
    string? Password,
    string? PrivateKey,
    string? PrivateKeyPassphrase,
    bool PreferStoredCredentials,
    int Columns,
    int Rows,
    string? WorkingDirectory = null);
