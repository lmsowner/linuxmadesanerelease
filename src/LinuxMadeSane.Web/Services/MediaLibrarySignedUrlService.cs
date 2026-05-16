// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace LinuxMadeSane.Web.Services;

public sealed class MediaLibrarySignedUrlService(IDataProtectionProvider dataProtectionProvider)
{
    private const int TokenByteLength = 18;

    private readonly ConcurrentDictionary<string, SignedUrlPayload> shortTokens = new(StringComparer.Ordinal);
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector("LinuxMadeSane.MediaLibrary.SignedUrls.v1");
    private readonly object cleanupGate = new();
    private DateTimeOffset nextCleanupUtc = DateTimeOffset.UtcNow;

    public string CreateStreamToken(Guid itemId, TimeSpan lifetime) =>
        CreateToken($"stream:{itemId:N}", lifetime);

    public bool ValidateStreamToken(Guid itemId, string? token) =>
        ValidateToken($"stream:{itemId:N}", token);

    public string CreatePlaylistToken(string scope, TimeSpan lifetime) =>
        CreateToken($"playlist:{scope}", lifetime);

    public bool ValidatePlaylistToken(string scope, string? token) =>
        ValidateToken($"playlist:{scope}", token);

    private string CreateToken(string scope, TimeSpan lifetime)
    {
        var now = DateTimeOffset.UtcNow;
        CleanupExpiredTokens(now);
        var payload = new SignedUrlPayload(scope, now.Add(lifetime));

        while (true)
        {
            var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenByteLength));
            if (shortTokens.TryAdd(token, payload))
            {
                return token;
            }
        }
    }

    private bool ValidateToken(string scope, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        CleanupExpiredTokens(now);
        if (shortTokens.TryGetValue(token, out var cachedPayload))
        {
            if (cachedPayload.ExpiresUtc < now)
            {
                shortTokens.TryRemove(token, out _);
                return false;
            }

            return string.Equals(cachedPayload.Scope, scope, StringComparison.Ordinal);
        }

        return ValidateProtectedToken(scope, token, now);
    }

    private bool ValidateProtectedToken(string scope, string token, DateTimeOffset now)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<SignedUrlPayload>(protector.Unprotect(token));
            return payload is not null &&
                   string.Equals(payload.Scope, scope, StringComparison.Ordinal) &&
                   payload.ExpiresUtc >= now;
        }
        catch
        {
            return false;
        }
    }

    private void CleanupExpiredTokens(DateTimeOffset now)
    {
        lock (cleanupGate)
        {
            if (now < nextCleanupUtc)
            {
                return;
            }

            nextCleanupUtc = now.AddMinutes(1);
            foreach (var token in shortTokens)
            {
                if (token.Value.ExpiresUtc < now)
                {
                    shortTokens.TryRemove(token.Key, out _);
                }
            }
        }
    }

    private sealed record SignedUrlPayload(string Scope, DateTimeOffset ExpiresUtc);
}
