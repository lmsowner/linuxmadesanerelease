// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Ai;

namespace LinuxMadeSane.Application.Interfaces;

public interface IRunbookAiDraftService
{
    Task<RunbookAiDraftResult> DraftAsync(
        RunbookAiDraftRequest request,
        CancellationToken cancellationToken = default);
}
