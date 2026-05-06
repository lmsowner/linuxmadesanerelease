namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record LocalUserPasswordResetViewModel(
    Guid UserId,
    string UserName,
    string SuggestedPassword);
