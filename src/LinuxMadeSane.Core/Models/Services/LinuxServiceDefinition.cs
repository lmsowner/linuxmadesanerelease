// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Services;

public sealed record LinuxServiceDefinition(
    Guid Id,
    string UnitName,
    string DisplayName,
    string HostName,
    string Summary,
    ServiceRuntimeState RuntimeState,
    ServiceHealthStatus HealthStatus,
    bool EnabledAtBoot,
    bool ActiveUnderSystemd,
    string RunningUser,
    string RunningGroup,
    string WorkingDirectory,
    string ExecStart,
    string? EnvironmentFile,
    int RestartCount,
    DateTimeOffset LastStartTime,
    int ListeningPort,
    IReadOnlyList<string> RecentErrors)
{
    public string UnitFileState { get; init; } = string.Empty;
    public string ActiveState { get; init; } = string.Empty;
    public string SubState { get; init; } = string.Empty;
    public string HealthReason { get; init; } = string.Empty;
    public bool IsRemoved { get; init; }
}
