// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record PackageState(
    string Name,
    bool IsInstalled,
    string Version,
    string Status);
