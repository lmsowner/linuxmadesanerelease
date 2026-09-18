// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace LinuxMadeSane.Web.Services;

public sealed class TemporarySetupAuthorizationService(IDataProtectionProvider dataProtectionProvider)
{
    public const string CookieName = "lms.temporary-setup";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector("linux-made-sane", "temporary-setup");

    public bool IsAuthorized(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var payload = protector.Unprotect(value);
            var separator = payload.IndexOf('|');
            return separator > 0 &&
                   long.TryParse(payload[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiry) &&
                   DateTimeOffset.UtcNow.ToUnixTimeSeconds() < expiry;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public void Authorize(HttpResponse response, bool secure)
    {
        var expiry = DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds();
        var payload = $"{expiry.ToString(CultureInfo.InvariantCulture)}|{Guid.NewGuid():N}";
        response.Cookies.Append(
            CookieName,
            protector.Protect(payload),
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Strict,
                Secure = secure,
                MaxAge = Lifetime,
                Expires = DateTimeOffset.UtcNow.Add(Lifetime),
                Path = "/"
            });
    }

    public void Clear(HttpResponse response) =>
        response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
}
