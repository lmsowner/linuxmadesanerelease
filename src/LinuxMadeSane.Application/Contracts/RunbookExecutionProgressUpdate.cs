// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts;

public sealed record RunbookExecutionProgressUpdate(
    Guid RunbookId,
    Guid HostId,
    string HostName,
    RunbookExecutionProgressKind Kind,
    string Message,
    string Content,
    int? ExitCode,
    bool IsCompleteSnapshot,
    DateTimeOffset OccurredAtUtc);
