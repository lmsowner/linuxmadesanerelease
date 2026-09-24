// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class FileBrowserShortcutEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ManagedHostId { get; set; }
    public string Label { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
