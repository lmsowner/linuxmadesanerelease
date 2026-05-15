namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record InitialSetupViewModel(
    bool IsComplete,
    bool CanStart,
    bool CanVerify,
    Guid? PendingUserId,
    string PendingEmail,
    string PendingLinuxUsername,
    int UserCount,
    string StatusMessage);
