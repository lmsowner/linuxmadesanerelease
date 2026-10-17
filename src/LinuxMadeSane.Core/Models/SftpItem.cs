// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record SftpItem(
    string Name,
    string FullPath,
    SftpItemType ItemType,
    long SizeBytes,
    DateTimeOffset? LastModifiedUtc,
    string Permissions,
    string OwnerName = "",
    string GroupName = "",
    string PermissionsOctal = "",
    string LinkTarget = "");
