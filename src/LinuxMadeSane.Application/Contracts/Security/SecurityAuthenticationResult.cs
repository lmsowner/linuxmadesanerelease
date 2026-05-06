namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record SecurityAuthenticationResult(
    bool Succeeded,
    Guid? UserId,
    string? Email,
    string? FailureMessage)
{
    public static SecurityAuthenticationResult Success(Guid userId, string email) =>
        new(true, userId, email, null);

    public static SecurityAuthenticationResult Failure(string message) =>
        new(false, null, null, message);
}
