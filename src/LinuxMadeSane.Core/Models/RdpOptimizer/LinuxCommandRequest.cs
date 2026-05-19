// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
}
