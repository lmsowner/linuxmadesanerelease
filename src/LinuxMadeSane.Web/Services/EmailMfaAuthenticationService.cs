// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using Microsoft.Extensions.Caching.Memory;

namespace LinuxMadeSane.Web.Services;

public sealed class EmailMfaAuthenticationService(
    ISecurityUserStore securityUserStore,
    IMessagingEmailSettingsStore messagingEmailSettingsStore,
    IEmailDeliveryService emailDeliveryService,
    IMemoryCache memoryCache,
    ILogger<EmailMfaAuthenticationService> logger)
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(10);
    private const int MaximumCodeAttempts = 5;
    private const string GenericSentMessage = "If that LMS account can receive email sign-in, check your inbox for the code or secure login link.";

    public async Task<EmailMfaRequestResult> SendLoginChallengeAsync(
        string email,
        string requestOrigin,
        string returnUrl,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return EmailMfaRequestResult.Accepted(GenericSentMessage);
        }

        var settings = await messagingEmailSettingsStore.GetAsync(cancellationToken);
        var user = await securityUserStore.FindByEmailAsync(normalizedEmail, cancellationToken);
        if (user is null || !user.IsEnabled || !CanSendEmailChallenge(settings))
        {
            return EmailMfaRequestResult.Accepted(GenericSentMessage);
        }

        var token = CreateToken();
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var challenge = new EmailMfaChallenge(
            user.Id,
            user.Email,
            ComputeCodeHash(code, token),
            token,
            DateTimeOffset.UtcNow.Add(ChallengeLifetime),
            0);

        RemoveExistingEmailChallenge(normalizedEmail);
        memoryCache.Set(BuildEmailChallengeKey(normalizedEmail), challenge, ChallengeLifetime);
        memoryCache.Set(BuildTokenChallengeKey(token), challenge, ChallengeLifetime);

        var loginLink = BuildLoginLink(requestOrigin, token, returnUrl);
        var html = BuildEmailHtml(user.Email, code, loginLink, ChallengeLifetime);
        var result = await emailDeliveryService.SendHtmlAsync(
            user.Email,
            "Your Linux Made Sane sign-in code",
            html,
            cancellationToken);

        if (!result.Succeeded)
        {
            RemoveChallenge(challenge);
            logger.LogWarning(
                "LMS email MFA challenge send did not complete for {Email}. Attempted={Attempted}. Message={Message}",
                user.Email,
                result.Attempted,
                result.Message);
        }

        return EmailMfaRequestResult.Accepted(GenericSentMessage);
    }

    public async Task<EmailMfaLoginResult> ValidateCodeAsync(
        string email,
        string code,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(normalizedEmail) ||
            !memoryCache.TryGetValue(BuildEmailChallengeKey(normalizedEmail), out EmailMfaChallenge? challenge) ||
            challenge is null)
        {
            return EmailMfaLoginResult.Failure("That email sign-in code expired or was not recognised.");
        }

        if (challenge.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            RemoveChallenge(challenge);
            return EmailMfaLoginResult.Failure("That email sign-in code expired. Send a fresh email and try again.");
        }

        if (challenge.Attempts >= MaximumCodeAttempts)
        {
            RemoveChallenge(challenge);
            return EmailMfaLoginResult.Failure("Too many email sign-in attempts. Send a fresh email and try again.");
        }

        var normalizedCode = NormalizeCode(code);
        if (string.IsNullOrWhiteSpace(normalizedCode) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(challenge.CodeHash),
                Encoding.UTF8.GetBytes(ComputeCodeHash(normalizedCode, challenge.Token))))
        {
            var updatedChallenge = challenge with { Attempts = challenge.Attempts + 1 };
            memoryCache.Set(BuildEmailChallengeKey(normalizedEmail), updatedChallenge, challenge.ExpiresAtUtc);
            memoryCache.Set(BuildTokenChallengeKey(challenge.Token), updatedChallenge, challenge.ExpiresAtUtc);
            return EmailMfaLoginResult.Failure("That email sign-in code was not valid.");
        }

        return await CompleteChallengeAsync(challenge, cancellationToken);
    }

    public async Task<EmailMfaLoginResult> ValidateTokenAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        var normalizedToken = token?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedToken) ||
            !memoryCache.TryGetValue(BuildTokenChallengeKey(normalizedToken), out EmailMfaChallenge? challenge) ||
            challenge is null)
        {
            return EmailMfaLoginResult.Failure("That email sign-in link expired or was not recognised.");
        }

        if (challenge.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            RemoveChallenge(challenge);
            return EmailMfaLoginResult.Failure("That email sign-in link expired. Send a fresh email and try again.");
        }

        return await CompleteChallengeAsync(challenge, cancellationToken);
    }

    private async Task<EmailMfaLoginResult> CompleteChallengeAsync(
        EmailMfaChallenge challenge,
        CancellationToken cancellationToken)
    {
        RemoveChallenge(challenge);
        var user = await securityUserStore.GetAsync(challenge.UserId, cancellationToken);
        if (user is null || !user.IsEnabled)
        {
            return EmailMfaLoginResult.Failure("That LMS account is no longer available.");
        }

        var now = DateTimeOffset.UtcNow;
        await securityUserStore.SaveAsync(user with
        {
            LastLoginAtUtc = now,
            UpdatedAtUtc = now
        }, cancellationToken);

        return EmailMfaLoginResult.Success(user);
    }

    private void RemoveExistingEmailChallenge(string normalizedEmail)
    {
        if (memoryCache.TryGetValue(BuildEmailChallengeKey(normalizedEmail), out EmailMfaChallenge? existing) &&
            existing is not null)
        {
            RemoveChallenge(existing);
        }
    }

    private void RemoveChallenge(EmailMfaChallenge challenge)
    {
        memoryCache.Remove(BuildEmailChallengeKey(NormalizeEmail(challenge.Email)));
        memoryCache.Remove(BuildTokenChallengeKey(challenge.Token));
    }

    private static bool CanSendEmailChallenge(Core.Models.Messaging.MessagingEmailSettings settings) =>
        settings.IsEnabled &&
        settings.Provider != MessagingEmailProvider.Disabled &&
        settings.LastVerifiedAtUtc.HasValue;

    private static string BuildLoginLink(string requestOrigin, string token, string returnUrl)
    {
        var origin = ResolveRequestOrigin(requestOrigin);
        var builder = new UriBuilder(origin)
        {
            Path = "/auth/email-mfa/login",
            Query = $"token={Uri.EscapeDataString(token)}&returnUrl={Uri.EscapeDataString(NormalizeReturnUrl(returnUrl))}"
        };

        return builder.Uri.ToString();
    }

    private static string ResolveRequestOrigin(string requestOrigin)
    {
        if (Uri.TryCreate(requestOrigin?.Trim(), UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https")
        {
            return uri.GetLeftPart(UriPartial.Authority);
        }

        return "http://localhost:5080";
    }

    private static string BuildEmailHtml(
        string email,
        string code,
        string loginLink,
        TimeSpan lifetime)
    {
        var encodedEmail = WebUtility.HtmlEncode(email);
        var encodedCode = WebUtility.HtmlEncode(code);
        var spacedCode = WebUtility.HtmlEncode(string.Join(" ", code.ToCharArray()));
        var encodedLoginLink = WebUtility.HtmlEncode(loginLink);
        var minutes = Math.Max(1, (int)Math.Round(lifetime.TotalMinutes));

        return $$"""
            <!doctype html>
            <html lang="en">
            <body style="margin:0;padding:0;background:#eef4f8;font-family:Inter,Segoe UI,Arial,sans-serif;color:#132235;">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#eef4f8;padding:30px 12px;">
                <tr>
                  <td align="center">
                    <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:640px;background:#ffffff;border:1px solid #d8e4ee;border-radius:20px;overflow:hidden;box-shadow:0 18px 46px rgba(21,39,55,.12);">
                      <tr>
                        <td style="background:#061522;padding:30px 34px;color:#ffffff;">
                          <div style="font-size:12px;letter-spacing:.08em;text-transform:uppercase;color:#28e0d2;font-weight:800;">Linux Made Sane</div>
                          <h1 style="margin:10px 0 0;font-size:30px;line-height:1.12;font-weight:850;">Secure sign-in</h1>
                          <p style="margin:10px 0 0;color:#c9dce7;font-size:15px;line-height:1.5;">Use this one-time code or open the secure link to continue.</p>
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:32px 34px 26px;">
                          <p style="margin:0 0 18px;font-size:16px;line-height:1.55;color:#31435a;">A sign-in was requested for <strong>{{encodedEmail}}</strong>.</p>
                          <div style="margin:0 0 20px;padding:22px;border:1px solid #cfe0ea;border-radius:18px;background:#f7fbfd;text-align:center;">
                            <p style="margin:0 0 10px;font-size:12px;font-weight:800;text-transform:uppercase;color:#5f7388;letter-spacing:.08em;">Email MFA code</p>
                            <div data-lms-email-code="{{encodedCode}}" style="font-size:34px;line-height:1.1;letter-spacing:.18em;font-weight:900;color:#071522;font-family:Consolas,Menlo,monospace;">{{spacedCode}}</div>
                          </div>
                          <p style="margin:0 0 22px;text-align:center;">
                            <a href="{{encodedLoginLink}}" style="display:inline-block;background:#087a64;color:#ffffff;text-decoration:none;padding:14px 22px;border-radius:12px;font-size:15px;font-weight:850;">Open Linux Made Sane</a>
                          </p>
                          <p style="margin:0;font-size:13px;line-height:1.55;color:#65778b;">This code and link expire in {{minutes}} minutes and work once. If you did not request this, ignore the email.</p>
                        </td>
                      </tr>
                    </table>
                  </td>
                </tr>
              </table>
            </body>
            </html>
            """;
    }

    private static string NormalizeEmail(string? email)
    {
        var trimmed = email?.Trim() ?? string.Empty;
        return MailAddress.TryCreate(trimmed, out var parsed)
            ? parsed.Address.ToLowerInvariant()
            : string.Empty;
    }

    private static string NormalizeCode(string? code)
    {
        var digits = new string((code ?? string.Empty).Where(char.IsDigit).Take(6).ToArray());
        return digits.Length == 6 ? digits : string.Empty;
    }

    private static string NormalizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return "/";
        }

        var trimmed = returnUrl.Trim();
        return trimmed.StartsWith("/", StringComparison.Ordinal) &&
               !trimmed.StartsWith("//", StringComparison.Ordinal) &&
               !trimmed.StartsWith("/\\", StringComparison.Ordinal)
            ? trimmed
            : "/";
    }

    private static string CreateToken() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string ComputeCodeHash(string code, string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{token}:{code}"));
        return Convert.ToHexString(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string BuildEmailChallengeKey(string normalizedEmail) => $"lms-email-mfa:email:{normalizedEmail}";

    private static string BuildTokenChallengeKey(string token) => $"lms-email-mfa:token:{token}";

    private sealed record EmailMfaChallenge(
        Guid UserId,
        string Email,
        string CodeHash,
        string Token,
        DateTimeOffset ExpiresAtUtc,
        int Attempts);
}

public sealed record EmailMfaRequestResult(
    bool Succeeded,
    string Message)
{
    public static EmailMfaRequestResult Accepted(string message) => new(true, message);
}

public sealed record EmailMfaLoginResult(
    bool Succeeded,
    SecurityUser? User,
    string? ErrorMessage)
{
    public static EmailMfaLoginResult Success(SecurityUser user) => new(true, user, null);

    public static EmailMfaLoginResult Failure(string message) => new(false, null, message);
}
