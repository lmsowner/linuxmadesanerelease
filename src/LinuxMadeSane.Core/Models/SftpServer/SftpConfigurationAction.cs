// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpConfigurationAction(
    int Order,
    string Title,
    string Summary,
    string CommandText,
    bool MutatesSystem,
    string? TargetPath,
    string? ProposedContent);
