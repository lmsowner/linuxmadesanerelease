// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record DesktopSessionConfiguration(
    string DefaultSession,
    string XrdpSessionCommand,
    string? DisplayManager,
    bool XrdpUsesXfce,
    IReadOnlyList<string> TouchedFiles);
