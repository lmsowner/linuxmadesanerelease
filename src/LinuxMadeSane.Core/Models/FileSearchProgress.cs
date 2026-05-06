namespace LinuxMadeSane.Core.Models;

public sealed record FileSearchProgress(
    string CurrentPath,
    DateTimeOffset ReportedAtUtc,
    FileSearchMatch? Match = null);
