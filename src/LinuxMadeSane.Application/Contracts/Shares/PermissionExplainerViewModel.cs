using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record PermissionExplainerViewModel(
    string Path,
    PermissionExplainerResult Explanation);
