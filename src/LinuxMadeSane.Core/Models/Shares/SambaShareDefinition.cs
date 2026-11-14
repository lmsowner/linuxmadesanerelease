// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
    string DirectoryMaskExplanation)
{
    public bool IsExternallyConfigured { get; init; }

    public string RemovalConfirmation => IsExternallyConfigured
        ? $"This share is in the Samba configuration and is not LMS-managed. Would you like me to remove {Name} for you? The files in {SharePath} will stay in place."
        : $"Remove share {Name}? The files in {SharePath} will stay in place.";
}
