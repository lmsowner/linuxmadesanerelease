namespace LinuxMadeSane.Core.Models.Shares;

public sealed record LinuxShareUser(
    Guid Id,
    string UserName,
    int Uid,
    int Gid,
    string DisplayName,
    string PrimaryGroup,
    IReadOnlyList<string> SupplementaryGroups,
    string HomeDirectory,
    string LoginShell,
    bool IsEnabled);
