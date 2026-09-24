// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
