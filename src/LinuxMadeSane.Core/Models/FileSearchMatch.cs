// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record FileSearchMatch(
    string Name,
    string FullPath,
    string ParentPath,
    SftpItemType ItemType,
    long SizeBytes,
    DateTimeOffset? LastModifiedUtc,
    bool MatchedContents = false,
    string LinkTarget = "");
