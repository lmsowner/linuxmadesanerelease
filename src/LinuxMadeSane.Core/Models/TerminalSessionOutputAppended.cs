using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record TerminalSessionOutputAppended(
    Guid SessionId,
    string Chunk,
    long OutputRevision,
    TerminalSessionStatus Status,
    DateTimeOffset LastActivityUtc);
