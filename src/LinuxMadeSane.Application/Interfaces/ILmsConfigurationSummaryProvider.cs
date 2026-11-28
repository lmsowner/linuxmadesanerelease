// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.SystemInfo;

namespace LinuxMadeSane.Application.Interfaces;

public interface ILmsConfigurationSummaryProvider
{
    string ModuleName { get; }

    int SortOrder { get; }

    string? NavigationUrl => null;

    Task<LmsConfigurationSummary> GetConfigurationSummaryAsync(
        CancellationToken cancellationToken = default);
}
