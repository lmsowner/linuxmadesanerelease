// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record LinuxCommandRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    bool RequiresSudo,
    TimeSpan Timeout,
    string Description,
    string? WorkingDirectory = null)
{
    public bool IsOptionalExternalTool { get; init; }

    public IReadOnlySet<int>? SensitiveArgumentIndexes { get; init; }

    public byte[]? StandardInputBytes { get; init; }
}
