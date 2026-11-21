// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpConfigurationAction(
    int Order,
    string Title,
    string Summary,
    string CommandText,
    bool MutatesSystem,
    string? TargetPath,
    string? ProposedContent);
