using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record SftpItem(
    string Name,
    string FullPath,
    SftpItemType ItemType,
    long SizeBytes,
    DateTimeOffset? LastModifiedUtc,
    string Permissions,
    string OwnerName = "",
    string GroupName = "",
    string PermissionsOctal = "",
    string LinkTarget = "");
