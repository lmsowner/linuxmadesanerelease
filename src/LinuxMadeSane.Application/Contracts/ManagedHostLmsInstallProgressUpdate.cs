// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Contracts;

public enum ManagedHostLmsInstallProgressState
{
    Starting,
    Output,
    Completed,
    Failed
}

public sealed record ManagedHostLmsInstallProgressUpdate(
    ManagedHostLmsInstallProgressState State,
    string Message,
    DateTimeOffset OccurredAtUtc,
    CommandExecutionOutputChannel? Channel = null);
