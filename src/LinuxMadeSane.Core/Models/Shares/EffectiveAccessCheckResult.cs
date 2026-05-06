namespace LinuxMadeSane.Core.Models.Shares;

public sealed record EffectiveAccessCheckResult(
    string Principal,
    IReadOnlyList<string> GroupMembership,
    Guid ShareId,
    bool CanAccessShare,
    bool CanListFiles,
    bool CanCreateFile,
    bool CanDeleteOwnFile,
    bool CanDeleteOthersFiles,
    IReadOnlyList<string> Reasons);
