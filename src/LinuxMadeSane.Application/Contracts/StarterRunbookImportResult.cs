// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts;

public sealed record StarterRunbookImportResult(
    string HostName,
    int ImportedCount,
    int ExistingCount);
