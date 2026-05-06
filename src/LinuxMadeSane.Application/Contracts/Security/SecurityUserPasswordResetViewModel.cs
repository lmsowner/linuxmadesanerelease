namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record SecurityUserPasswordResetViewModel(
    Guid UserId,
    string Email,
    string LinuxUsername,
    string SuggestedPassword);
