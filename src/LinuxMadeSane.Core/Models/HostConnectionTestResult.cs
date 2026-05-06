using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record HostConnectionTestResult(
    ConnectionTestStatus Status,
    string Summary,
    string? Detail,
    DateTimeOffset CheckedAtUtc);
