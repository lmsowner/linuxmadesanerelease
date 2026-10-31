// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Core.Abstractions;

public interface IWebResearchService
{
    Task<SearchWebToolResponse> SearchAsync(
        SearchWebToolRequest request,
        CancellationToken cancellationToken = default);

    Task<FetchWebPageToolResponse> FetchPageAsync(
        FetchWebPageToolRequest request,
        CancellationToken cancellationToken = default);
}
