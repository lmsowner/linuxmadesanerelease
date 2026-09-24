// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Services;

public sealed partial class AiPromptSanitizer : IAiPromptSanitizer
{
    public bool IsTrustedLocalProvider(AiProviderType providerType) =>
        providerType == AiProviderType.Ollama;

    public AiPromptSanitizationResult Sanitize(
        string content,
        AiProviderType providerType,
        IEnumerable<string>? additionalSecrets = null)
    {
        var normalizedContent = content ?? string.Empty;
        if (normalizedContent.Length == 0)
        {
            return new AiPromptSanitizationResult(
                normalizedContent,
                new AiPromptSanitizationSummary(false, IsTrustedLocalProvider(providerType), 0, [], string.Empty));
        }

        if (IsTrustedLocalProvider(providerType))
        {
            return new AiPromptSanitizationResult(
                normalizedContent,
                new AiPromptSanitizationSummary(false, true, 0, [], string.Empty));
        }

        var redactionCount = 0;
        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sanitized = normalizedContent;

        sanitized = ReplacePattern(
            sanitized,
            PrivateKeyBlockRegex(),
            "[REDACTED PRIVATE KEY]",
            "private key",
            categories,
            ref redactionCount);

        sanitized = ReplacePattern(
            sanitized,
            SshPassPasswordRegex(),
            "${prefix}[REDACTED]",
            "password",
            categories,
            ref redactionCount);

        sanitized = ReplacePattern(
            sanitized,
            AuthorizationHeaderRegex(),
            "${prefix}[REDACTED]",
            "authorization token",
            categories,
            ref redactionCount);

        sanitized = ReplacePattern(
            sanitized,
            CookieHeaderRegex(),
            "${prefix}[REDACTED]",
            "cookie",
            categories,
            ref redactionCount);

        sanitized = ReplacePattern(
            sanitized,
            UrlCredentialRegex(),
            "${prefix}[REDACTED]${suffix}",
            "credential",
            categories,
            ref redactionCount);

        sanitized = ReplacePattern(
            sanitized,
            SecretAssignmentRegex(),
            "${prefix}[REDACTED]",
            "secret assignment",
            categories,
            ref redactionCount);

        sanitized = ReplacePattern(
            sanitized,
            SecretFlagRegex(),
            "${prefix}[REDACTED]",
            "secret flag",
            categories,
            ref redactionCount);

        sanitized = ReplacePattern(
            sanitized,
            BearerTokenRegex(),
            "${prefix}[REDACTED]",
            "bearer token",
            categories,
            ref redactionCount);

        var literalSecrets = additionalSecrets?
            .Where(secret => !string.IsNullOrWhiteSpace(secret))
            .Select(secret => secret.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(secret => secret.Length)
            .ToArray() ?? [];

        foreach (var literalSecret in literalSecrets)
        {
            if (literalSecret.Length < 4)
            {
                continue;
            }

            var matches = Regex.Matches(sanitized, Regex.Escape(literalSecret), RegexOptions.CultureInvariant);
            if (matches.Count == 0)
            {
                continue;
            }

            sanitized = Regex.Replace(
                sanitized,
                Regex.Escape(literalSecret),
                "[REDACTED SECRET VALUE]",
                RegexOptions.CultureInvariant);
            redactionCount += matches.Count;
            categories.Add("secret value");
        }

        var applied = redactionCount > 0;
        var categoryList = categories.OrderBy(category => category, StringComparer.OrdinalIgnoreCase).ToArray();
        var message = applied
            ? $"Sensitive data was redacted before sending this prompt to the AI provider ({redactionCount} redaction{(redactionCount == 1 ? string.Empty : "s")}: {string.Join(", ", categoryList)})."
            : string.Empty;

        return new AiPromptSanitizationResult(
            sanitized,
            new AiPromptSanitizationSummary(applied, false, redactionCount, categoryList, message));
    }

    private static string ReplacePattern(
        string input,
        Regex regex,
        string replacement,
        string category,
        ISet<string> categories,
        ref int redactionCount)
    {
        var matches = regex.Matches(input);
        if (matches.Count == 0)
        {
            return input;
        }

        redactionCount += matches.Count;
        categories.Add(category);
        return regex.Replace(input, replacement);
    }

    [GeneratedRegex("-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*?-----END [A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyBlockRegex();

    [GeneratedRegex("(?i)(?<prefix>\\bsshpass\\s+-p\\s+)(\"[^\"]*\"|'[^']*'|\\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex SshPassPasswordRegex();

    [GeneratedRegex("(?im)(?<prefix>^\\s*authorization\\s*:\\s*bearer\\s+)(\\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeaderRegex();

    [GeneratedRegex("(?im)(?<prefix>^\\s*cookie\\s*:\\s*)(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex CookieHeaderRegex();

    [GeneratedRegex("(?i)(?<prefix>\\b[a-z][a-z0-9+.-]*://[^\\s/@:]+:)([^@\\s/]+)(?<suffix>@)", RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentialRegex();

    [GeneratedRegex("(?im)(?<prefix>\\b(?:password|passwd|passphrase|token|secret|api[_-]?key|access[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|session[_-]?token|cookie)\\b\\s*[:=]\\s*)(\"[^\"]*\"|'[^']*'|[^\\s,;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex("(?i)(?<prefix>--(?:password|passwd|passphrase|token|secret|api[-_]?key|access[-_]?key|access[-_]?token|refresh[-_]?token|client[-_]?secret)\\s+)(\"[^\"]*\"|'[^']*'|\\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex SecretFlagRegex();

    [GeneratedRegex("(?i)(?<prefix>\\bbearer\\s+)([a-z0-9._~+/=-]{8,})", RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();
}
