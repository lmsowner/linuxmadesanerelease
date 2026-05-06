namespace LinuxMadeSane.Core.Models;

public sealed record FileOwnershipPermissionsChangeRequest(
    string Path,
    string? OwnerName,
    string? GroupName,
    string? PermissionsOctal,
    bool Recursive);
