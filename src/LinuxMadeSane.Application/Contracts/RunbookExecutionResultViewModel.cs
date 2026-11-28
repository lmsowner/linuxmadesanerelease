// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts;

public sealed record RunbookExecutionResultViewModel(
    Guid RunbookId,
    string RunbookName,
    IReadOnlyList<RunbookExecutionHostResultViewModel> HostResults)
{
    public bool AllSucceeded =>
        HostResults.Count > 0 && HostResults.All(result => result.Success);

    public int SucceededCount =>
        HostResults.Count(result => result.Success);

    public int FailedCount =>
        HostResults.Count - SucceededCount;
}
