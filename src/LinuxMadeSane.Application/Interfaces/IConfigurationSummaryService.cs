// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.SystemInfo;

namespace LinuxMadeSane.Application.Interfaces;

public interface IConfigurationSummaryService
{
    Task<LmsConfigurationSummarySnapshot> GetSummariesAsync(
        CancellationToken cancellationToken = default);

    string RenderPlainText(LmsConfigurationSummarySnapshot snapshot);
}
