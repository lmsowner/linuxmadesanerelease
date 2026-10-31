// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record FileOwnershipPermissionsChangeRequest(
    string Path,
    string? OwnerName,
    string? GroupName,
    string? PermissionsOctal,
    bool Recursive);
