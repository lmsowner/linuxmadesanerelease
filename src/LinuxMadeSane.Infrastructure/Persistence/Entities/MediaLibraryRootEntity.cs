// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class MediaLibraryRootEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int Category { get; set; }
    public string CustomCategoryName { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool Recursive { get; set; }
    public string IncludeExtensionsJson { get; set; } = "[]";
    public string ExcludeExtensionsJson { get; set; } = "[]";
    public string ExcludeFoldersJson { get; set; } = "[]";
    public int SortOrder { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? LastScanUtc { get; set; }
    public int LastScanStatus { get; set; }
    public string LastScanMessage { get; set; } = string.Empty;

    public List<MediaItemEntity> Items { get; } = [];
}
