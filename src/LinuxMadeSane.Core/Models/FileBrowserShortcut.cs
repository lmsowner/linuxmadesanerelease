// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models;

public sealed record FileBrowserShortcut(
    Guid Id,
    Guid UserId,
    Guid ManagedHostId,
    string Label,
    string TargetPath,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
