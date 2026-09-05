// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
