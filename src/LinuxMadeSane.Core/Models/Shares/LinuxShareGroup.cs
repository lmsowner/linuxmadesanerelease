namespace LinuxMadeSane.Core.Models.Shares;

public sealed record LinuxShareGroup(
    Guid Id,
    string GroupName,
    int Gid,
    string Description,
    IReadOnlyList<string> Members);
