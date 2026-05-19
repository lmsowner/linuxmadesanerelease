// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record ShareToolingInstallResult(
    bool Success,
    string HostName,
    string StatusMessage,
    IReadOnlyList<string> RequestedPackageNames,
    IReadOnlyList<OperationLogEntry> OperationLogs);
