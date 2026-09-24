// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Ai;

namespace LinuxMadeSane.Application.Interfaces;

public interface IRunbookAiDraftService
{
    Task<RunbookAiDraftResult> DraftAsync(
        RunbookAiDraftRequest request,
        CancellationToken cancellationToken = default);
}
