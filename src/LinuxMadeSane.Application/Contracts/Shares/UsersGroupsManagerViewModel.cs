using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record UsersGroupsManagerViewModel(
    IReadOnlyList<LinuxShareUser> Users,
    IReadOnlyList<LinuxShareGroup> Groups,
    IReadOnlyList<LocalUserAccessViewModel> UserAccessPolicies);
