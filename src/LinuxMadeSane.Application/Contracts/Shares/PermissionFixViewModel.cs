using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record PermissionFixViewModel(
    string Path,
    IReadOnlyList<PermissionIssue> Issues);
