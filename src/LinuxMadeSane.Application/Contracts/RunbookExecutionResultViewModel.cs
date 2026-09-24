// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
