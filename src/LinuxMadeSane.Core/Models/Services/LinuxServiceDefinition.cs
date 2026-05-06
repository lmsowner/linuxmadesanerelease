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
    IReadOnlyList<string> RecentErrors);
