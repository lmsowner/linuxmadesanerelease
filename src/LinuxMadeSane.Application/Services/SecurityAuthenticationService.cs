using LinuxMadeSane.Application.Contracts.Security;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;

namespace LinuxMadeSane.Application.Services;

public sealed class SecurityAuthenticationService(
    ISecurityUserStore securityUserStore,
    ISecretStore secretStore) : ISecurityAuthenticationService
{
    public async Task<SecurityAuthenticationResult> ValidateOtpAsync(string identifier, string otpCode, CancellationToken cancellationToken = default)
    {
        var normalizedIdentifier = identifier.Trim();
        if (string.IsNullOrWhiteSpace(normalizedIdentifier))
        {
            return SecurityAuthenticationResult.Failure("Enter the username or email registered for this authenticator user.");
        }

        var users = await securityUserStore.ListAsync(cancellationToken);
        if (users.Count == 0)
        {
            return SecurityAuthenticationResult.Failure("No authenticator users are registered yet. Provision one from an enabled interface first.");
        }

        var user = await securityUserStore.FindByEmailAsync(normalizedIdentifier.ToLowerInvariant(), cancellationToken)
            ?? await securityUserStore.FindByLinuxUsernameAsync(normalizedIdentifier.ToLowerInvariant(), cancellationToken);
        if (user is null)
        {
            return SecurityAuthenticationResult.Failure("That username or email is not registered for remote login.");
        }

        if (!user.IsEnabled)
        {
            return SecurityAuthenticationResult.Failure("This authenticator user is disabled.");
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

        return SecurityAuthenticationResult.Success(user.Id, user.Email);
    }
}
