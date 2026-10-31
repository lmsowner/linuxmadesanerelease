// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record ShareToolingInstallResult(
    bool Success,
    string HostName,
    string StatusMessage,
    IReadOnlyList<string> RequestedPackageNames,
    IReadOnlyList<OperationLogEntry> OperationLogs);
