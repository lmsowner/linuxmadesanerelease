namespace LinuxMadeSane.Core.Models;

public sealed record FileBrowserShortcut(
    Guid Id,
    Guid UserId,
    Guid ManagedHostId,
    string Label,
    string TargetPath,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
