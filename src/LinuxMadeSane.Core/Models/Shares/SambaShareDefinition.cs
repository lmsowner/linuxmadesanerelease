namespace LinuxMadeSane.Core.Models.Shares;

public sealed record SambaShareDefinition(
    Guid Id,
    string Name,
    string SharePath,
    string Description,
    bool Browseable,
    bool ReadOnly,
    bool GuestAccess,
    IReadOnlyList<string> ValidUsers,
    IReadOnlyList<string> ValidGroups,
    IReadOnlyList<string> WriteList,
    IReadOnlyList<string> ReadList,
    string? ForceUser,
    string? ForceGroup,
    string CreateMask,
    string DirectoryMask,
    string CreateMaskExplanation,
    string DirectoryMaskExplanation);
