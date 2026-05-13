using LinuxMadeSane.Application.Contracts.Security;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;

namespace LinuxMadeSane.Application.Services;

public sealed class SecurityAuthenticationService(
    ISecurityUserStore securityUserStore,
    ISecretStore secretStore) : ISecurityAuthenticationService
{
    public async Task<SecurityAuthenticationResult> ValidateOtpAsync(string email, string otpCode, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return SecurityAuthenticationResult.Failure("Enter the email registered for this LMS account.");
        }

        var users = await securityUserStore.ListAsync(cancellationToken);
        if (users.Count == 0)
        {
            return SecurityAuthenticationResult.Failure("No LMS accounts are registered yet. Provision one from an enabled interface first.");
        }

        var user = await securityUserStore.FindByEmailAsync(normalizedEmail, cancellationToken);
        if (user is null)
        {
            return SecurityAuthenticationResult.Failure("That email is not registered for an LMS account.");
        }

        if (!user.IsEnabled)
        {
            return SecurityAuthenticationResult.Failure("This LMS account is disabled.");
        }

        var secret = await secretStore.ResolveSecretAsync(user.OtpSecretReference, cancellationToken);
        if (string.IsNullOrWhiteSpace(secret))
        {
            return SecurityAuthenticationResult.Failure("The registered authenticator secret could not be loaded.");
        }

        if (!TotpAuthenticator.ValidateCode(secret, otpCode))
        {
            return SecurityAuthenticationResult.Failure("The OTP code was not valid.");
        }

        await securityUserStore.SaveAsync(user with
        {
            LastLoginAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);

        return SecurityAuthenticationResult.Success(user.Id, user.Email, user.SessionLifetimeMinutes);
    }
}
