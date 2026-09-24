// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Services;

public sealed class ServiceEditor
{
    public Guid? Id { get; set; }

    [Required]
    public string UnitName { get; set; } = string.Empty;

    [Required]
    public string DisplayName { get; set; } = string.Empty;

    [Required]
    public string HostName { get; set; } = "local-machine";

    [Required]
    public string Summary { get; set; } = string.Empty;

    public ServiceRuntimeState RuntimeState { get; set; } = ServiceRuntimeState.Running;
    public ServiceHealthStatus HealthStatus { get; set; } = ServiceHealthStatus.Healthy;
    public bool EnabledAtBoot { get; set; } = true;
    public bool ActiveUnderSystemd { get; set; } = true;

    [Required]
    public string RunningUser { get; set; } = string.Empty;

    [Required]
    public string RunningGroup { get; set; } = string.Empty;

    [Required]
    public string WorkingDirectory { get; set; } = string.Empty;

    [Required]
    public string ExecStart { get; set; } = string.Empty;

    public string EnvironmentFile { get; set; } = string.Empty;
    public int RestartCount { get; set; }
    public DateTimeOffset LastStartTime { get; set; } = DateTimeOffset.Now;
    public int ListeningPort { get; set; }
    public string RecentErrorsCsv { get; set; } = string.Empty;
}
