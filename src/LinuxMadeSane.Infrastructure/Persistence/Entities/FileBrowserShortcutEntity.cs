// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
