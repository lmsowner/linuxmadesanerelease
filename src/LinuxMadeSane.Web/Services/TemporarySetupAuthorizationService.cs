// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace LinuxMadeSane.Web.Services;

public sealed class TemporarySetupAuthorizationService(IDataProtectionProvider dataProtectionProvider)
{
    public const string CookieName = "lms.temporary-setup";
    internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector("linux-made-sane", "temporary-setup");

    public string? GetAuthorizationId(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var payload = protector.Unprotect(value);
            var separator = payload.IndexOf('|');
            return separator > 0 &&
                   long.TryParse(payload[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiry) &&
                   DateTimeOffset.UtcNow.ToUnixTimeSeconds() < expiry
                ? payload[(separator + 1)..] : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void Authorize(HttpResponse response, bool secure, string authorizationId, DateTimeOffset expiresAtUtc)
    {
        var expiry = expiresAtUtc.ToUnixTimeSeconds();
        var payload = $"{expiry.ToString(CultureInfo.InvariantCulture)}|{authorizationId}";
        response.Cookies.Append(
            CookieName,
            protector.Protect(payload),
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Strict,
                Secure = secure,
                MaxAge = expiresAtUtc - DateTimeOffset.UtcNow,
                Expires = expiresAtUtc,
                Path = "/"
            });
    }

    public void Clear(HttpResponse response) =>
        response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
}
