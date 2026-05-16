// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

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
