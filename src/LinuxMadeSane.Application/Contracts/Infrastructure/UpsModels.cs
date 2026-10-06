// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.Infrastructure;
public sealed class UpsPolicy
{
    public string Source { get; set; } = "";
    public bool Enabled { get; set; }
    public int StopContainersAfterMinutes { get; set; } = 5;
    public List<Guid> ContainerIds { get; set; } = [];
    public int StopServicesAfterMinutes { get; set; } = 15;
    public List<string> Services { get; set; } = [];
    public int ShutdownBelowPercent { get; set; } = 20;
    public bool ShutdownHost { get; set; }
    public bool ShutdownConfirmed { get; set; }
}
public sealed record UpsSnapshot(string Source, bool Available, IReadOnlyDictionary<string, string> Values,
    string Message, DateTimeOffset CheckedUtc);
public sealed record UpsWorkspace(UpsPolicy Policy, IReadOnlyList<string> LocalSources,
    string ExistingConfiguration, IReadOnlyList<string> Events, string HardwareScan);
