// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpValidationResult(
    bool IsValid,
    string Summary,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    string? CommandText);
