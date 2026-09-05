// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.SystemInfo;

public enum LmsConfigurationSummaryStatus
{
    NotConfigured = 0,
    Configured = 1,
    Warning = 2,
    Error = 3,
    Disabled = 4
}

public sealed record LmsConfigurationSummaryItem(
    string Label,
    string Value,
    LmsConfigurationSummaryStatus? Status = null,
    bool IsSensitive = false);

public sealed record LmsConfigurationSummary(
    string ModuleName,
    LmsConfigurationSummaryStatus Status,
    string Description,
    IReadOnlyList<LmsConfigurationSummaryItem> Items,
    IReadOnlyList<string> Warnings,
    string? NavigationUrl,
    int SortOrder);

public sealed record LmsConfigurationSummarySnapshot(
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<LmsConfigurationSummary> Summaries);
