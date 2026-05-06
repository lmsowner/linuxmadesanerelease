namespace LinuxMadeSane.Core.Models.Shares;

public sealed record PermissionExplainerResult(
    string Path,
    string Owner,
    string Group,
    string Mode,
    bool HasAclEntries,
    string PlainEnglishSummary,
    bool CanRead,
    bool CanWrite,
    bool CanCreate,
    bool CanDelete,
    bool CanRename,
    IReadOnlyList<string> Conflicts);
