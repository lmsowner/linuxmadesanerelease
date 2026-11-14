// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
