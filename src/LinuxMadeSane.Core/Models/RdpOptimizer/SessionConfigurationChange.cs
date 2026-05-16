// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record SessionConfigurationChange(
    string FilePath,
    string Description,
    string ContentPreview,
    bool RequiresBackup,
    bool IsDestructive);
