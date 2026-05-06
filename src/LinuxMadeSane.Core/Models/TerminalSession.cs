using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record TerminalSession(
    Guid Id,
    Guid HostId,
    TerminalSessionStatus Status,
    string WorkingDirectory,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastActivityUtc);
