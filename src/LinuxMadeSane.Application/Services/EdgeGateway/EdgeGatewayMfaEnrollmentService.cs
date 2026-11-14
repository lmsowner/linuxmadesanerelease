// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Abstractions;

namespace LinuxMadeSane.Application.Services.EdgeGateway;

/// <summary>Enrols an existing LMS account for a configured service hostname, after proof of possession.</summary>
public sealed class EdgeGatewayMfaEnrollmentService(
    IEdgeGatewayStore routes, ISecurityUserStore users, ISecretStore secrets)
{
    private static readonly SemaphoreSlim SaveLock = new(1, 1);
    private PendingEnrollment? pending;
    private int attempts;

    public void Cancel() { pending = null; attempts = 0; }

    public async Task<EnrollmentQr> BeginAsync(Guid routeId, Guid userId, CancellationToken cancellationToken = default)
    {
        Cancel();
        var route = await routes.GetRouteAsync(routeId, cancellationToken)
            ?? throw new InvalidOperationException("This service no longer exists.");
        var user = await users.GetAsync(userId, cancellationToken);
        if (user is not { IsEnabled: true }) throw new InvalidOperationException("Select an enabled LMS account.");
        var hostname = route.Hostname.Trim().TrimEnd('.').ToLowerInvariant();
        if (Uri.CheckHostName(hostname) == UriHostNameType.Unknown)
            throw new InvalidOperationException("The service needs a valid external hostname.");
        // Reopening setup never rotates an authenticator that is already working.
        var secret = user.DomainOtpSecretReferences.TryGetValue(hostname, out var reference)
            ? await secrets.ResolveSecretAsync(reference, cancellationToken) : null;
        if (string.IsNullOrWhiteSpace(secret)) secret = TotpAuthenticator.GenerateSecret();
        pending = new(routeId, userId, hostname, secret, DateTimeOffset.UtcNow.AddMinutes(10));
        return new(hostname, user.Email,
            TotpAuthenticator.BuildOtpUri(user.Email, secret, $"LMS ({hostname})"),
            TotpAuthenticator.FormatManualEntryKey(secret));
    }

    public async Task ConfirmAsync(string code, CancellationToken cancellationToken = default)
    {
        await SaveLock.WaitAsync(cancellationToken);
        try
        {
            var enrollment = pending;
            if (enrollment is null || enrollment.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                Cancel();
                throw new InvalidOperationException("Setup expired. Select the email again to get a new QR code.");
            }
            if (!TotpAuthenticator.ValidateCode(enrollment.Secret, code))
            {
                if (++attempts >= 5) Cancel();
                throw new InvalidOperationException(attempts == 0
                    ? "Too many incorrect codes. Select the email again to restart setup."
                    : "That code did not match. Enter the current six-digit code from your authenticator.");
            }
            var route = await routes.GetRouteAsync(enrollment.RouteId, cancellationToken);
            if (route is null || !string.Equals(route.Hostname.TrimEnd('.'), enrollment.Hostname, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The service address changed. Close this dialog and start again.");
            var user = await users.GetAsync(enrollment.UserId, cancellationToken);
            if (user is not { IsEnabled: true }) throw new InvalidOperationException("This account is no longer enabled.");
            // Another setup may have completed while this QR was open. Do not replace it silently.
            var existingReference = user.DomainOtpSecretReferences.GetValueOrDefault(enrollment.Hostname);
            var existing = existingReference is null ? null : await secrets.ResolveSecretAsync(existingReference, cancellationToken);
            if (!string.IsNullOrWhiteSpace(existing) && !string.Equals(existing, enrollment.Secret, StringComparison.Ordinal))
                throw new InvalidOperationException("MFA changed while this dialog was open. Select the email again.");
            if (string.IsNullOrWhiteSpace(existing))
            {
                var reference = await secrets.StoreSecretAsync(enrollment.Secret, $"domain-mfa:{user.Id:N}:{enrollment.Hostname}", cancellationToken);
                try
                {
                    var references = new Dictionary<string, string>(user.DomainOtpSecretReferences) { [enrollment.Hostname] = reference };
                    await users.SaveAsync(user with { DomainOtpSecretReferences = references, UpdatedAtUtc = DateTimeOffset.UtcNow }, cancellationToken);
                }
                catch
                {
                    await secrets.DeleteSecretAsync(reference, CancellationToken.None);
                    throw;
                }
            }
            Cancel();
        }
        finally { SaveLock.Release(); }
    }

    private sealed record PendingEnrollment(Guid RouteId, Guid UserId, string Hostname, string Secret, DateTimeOffset ExpiresAtUtc);
    public sealed record EnrollmentQr(string Hostname, string Email, string OtpUri, string ManualEntryKey);
}
