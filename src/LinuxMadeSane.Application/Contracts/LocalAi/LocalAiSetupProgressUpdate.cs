// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.LocalAi;

public enum LocalAiSetupProgressState
{
    Running = 0,
    Completed = 1,
    Failed = 2
}

public sealed record LocalAiSetupProgressUpdate(
    string Step,
    string Detail,
    LocalAiSetupProgressState State,
    DateTimeOffset ReportedAtUtc);
