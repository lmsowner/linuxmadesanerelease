// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record FileSearchResponse(
    string RootPath,
    IReadOnlyList<FileSearchMatch> Results,
    bool LimitReached,
    string? Warning = null)
{
    public bool PdfSearchSupportRequired { get; init; }
    public IReadOnlyList<FileSearchMatch> PendingPdfFiles { get; init; } = [];
}
