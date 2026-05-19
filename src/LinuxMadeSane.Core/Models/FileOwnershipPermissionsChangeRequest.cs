// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models;

public sealed record FileOwnershipPermissionsChangeRequest(
    string Path,
    string? OwnerName,
    string? GroupName,
    string? PermissionsOctal,
    bool Recursive);
