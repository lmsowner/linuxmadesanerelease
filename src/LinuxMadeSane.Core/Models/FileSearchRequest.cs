// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models;

public sealed record FileSearchRequest(
    string RootPath,
    string? NamePattern = null,
    bool IncludeFiles = true,
    bool IncludeFolders = true,
    long? MinimumSizeBytes = null,
    long? MaximumSizeBytes = null,
    DateTimeOffset? ModifiedFromUtc = null,
    DateTimeOffset? ModifiedToUtc = null,
    DateTimeOffset? CreatedFromUtc = null,
    DateTimeOffset? CreatedToUtc = null,
    DateTimeOffset? AccessedFromUtc = null,
    DateTimeOffset? AccessedToUtc = null,
    string? ContainsText = null,
    bool CaseInsensitive = true,
    int MaxResults = 500)
{
    public bool RequiresContentScan => !string.IsNullOrWhiteSpace(ContainsText);

    public bool UsesNameFilter => !string.IsNullOrWhiteSpace(NamePattern);

    public bool UsesSizeFilter => MinimumSizeBytes.HasValue || MaximumSizeBytes.HasValue;

    public bool UsesDateFilters =>
        ModifiedFromUtc.HasValue ||
        ModifiedToUtc.HasValue ||
        CreatedFromUtc.HasValue ||
        CreatedToUtc.HasValue ||
        AccessedFromUtc.HasValue ||
        AccessedToUtc.HasValue;
}
