// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.SystemInfo;

namespace LinuxMadeSane.Application.Interfaces;

public interface IConfigurationSummaryService
{
    Task<LmsConfigurationSummarySnapshot> GetSummariesAsync(
        CancellationToken cancellationToken = default);

    string RenderPlainText(LmsConfigurationSummarySnapshot snapshot);
}
