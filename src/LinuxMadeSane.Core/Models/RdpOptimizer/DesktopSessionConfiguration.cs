// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record DesktopSessionConfiguration(
    string DefaultSession,
    string XrdpSessionCommand,
    string? DisplayManager,
    bool XrdpUsesXfce,
    IReadOnlyList<string> TouchedFiles);
