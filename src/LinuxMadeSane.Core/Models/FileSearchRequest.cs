// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
    int MaxResults = 500,
    FileSearchContentTypes ContentTypes = FileSearchContentTypes.All,
    bool IncludeSubfolders = true,
    IReadOnlyList<string>? CandidateNames = null)
{
    // Inline browser name filters are literal and case-insensitive, independently of document contents.
    public string? NameContains { get; init; }
    public bool RequiresContentScan => !string.IsNullOrWhiteSpace(ContainsText);

    public bool UsesNameFilter => !string.IsNullOrWhiteSpace(NamePattern) || !string.IsNullOrWhiteSpace(NameContains);

    public bool UsesSizeFilter => MinimumSizeBytes.HasValue || MaximumSizeBytes.HasValue;

    public bool UsesDateFilters =>
        ModifiedFromUtc.HasValue ||
        ModifiedToUtc.HasValue ||
        CreatedFromUtc.HasValue ||
        CreatedToUtc.HasValue ||
        AccessedFromUtc.HasValue ||
        AccessedToUtc.HasValue;
}

[Flags]
public enum FileSearchContentTypes { None = 0, Pdf = 1, Text = 2, Logs = 4, All = Pdf | Text | Logs }
