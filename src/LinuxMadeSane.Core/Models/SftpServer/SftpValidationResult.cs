// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpValidationResult(
    bool IsValid,
    string Summary,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    string? CommandText);
