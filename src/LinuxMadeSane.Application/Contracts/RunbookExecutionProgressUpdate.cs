// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
