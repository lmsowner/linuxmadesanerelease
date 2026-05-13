namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record SecurityMessagingTestResult(
    bool Succeeded,
    bool Attempted,
    string Message);
