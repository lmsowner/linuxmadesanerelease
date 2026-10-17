// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record TerminalConnectionRequest(
    string Username,
    string? Password,
    string? PrivateKey,
    string? PrivateKeyPassphrase,
    bool PreferStoredCredentials,
    int Columns,
    int Rows,
    string? WorkingDirectory = null,
    Guid? OwnerId = null)
{
    public Action<string>? Progress { get; init; }
}
