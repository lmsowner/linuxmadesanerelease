// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Security;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;

namespace LinuxMadeSane.Application.Services;

public sealed class SecurityAuthenticationService(
    ISecurityUserStore securityUserStore,
    ISecretStore secretStore) : ISecurityAuthenticationService
{
    public async Task<SecurityAuthenticationResult> ValidateOtpAsync(string email, string otpCode, CancellationToken cancellationToken = default)
        => await ValidateAsync(email, otpCode, null, cancellationToken);

    public Task<SecurityAuthenticationResult> ValidateDomainOtpAsync(string email, string otpCode, string hostname, CancellationToken cancellationToken = default) =>
        ValidateAsync(email, otpCode, hostname.Trim().TrimEnd('.').ToLowerInvariant(), cancellationToken);

    private async Task<SecurityAuthenticationResult> ValidateAsync(string email, string otpCode, string? hostname, CancellationToken cancellationToken)
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

        var reference = hostname is not null && user.DomainOtpSecretReferences.TryGetValue(hostname, out var domainReference)
            ? domainReference : user.OtpSecretReference;
        var secret = await secretStore.ResolveSecretAsync(reference, cancellationToken);
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
