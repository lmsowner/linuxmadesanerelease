// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Text.RegularExpressions;

namespace LinuxMadeSane.Application.Services.Cloudflare;

public static partial class CloudflareHostnameValidator
{
    [GeneratedRegex("^[a-z0-9-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();

    public static bool TryNormalizeRelativeHostname(
        string? value,
        out string normalizedValue,
        out string? error)
    {
        normalizedValue = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Enter a subdomain for the public hostname.";
            return false;
        }

        var candidate = value.Trim().Trim('.').ToLowerInvariant();
        if (candidate.Length == 0)
        {
            error = "Enter a subdomain for the public hostname.";
            return false;
        }

        if (candidate.Contains('*', StringComparison.Ordinal) ||
            candidate.Contains(' ', StringComparison.Ordinal) ||
            candidate.Contains('/', StringComparison.Ordinal))
        {
            error = "Use only DNS labels such as app, admin, or apps.tools.";
            return false;
        }

        var labels = candidate.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (labels.Length == 0 || string.Join(".", labels) != candidate)
        {
            error = "The hostname contains an empty label.";
            return false;
        }

        foreach (var label in labels)
        {
            if (label.Length is < 1 or > 63)
            {
                error = "Each hostname label must be between 1 and 63 characters.";
                return false;
            }

            if (label.StartsWith("-", StringComparison.Ordinal) ||
                label.EndsWith("-", StringComparison.Ordinal) ||
                !LabelPattern().IsMatch(label))
            {
                error = "Hostname labels may only contain lowercase letters, digits, and hyphens, and cannot start or end with a hyphen.";
                return false;
            }
        }

        normalizedValue = candidate;
        return true;
    }

    public static string BuildAbsoluteHostname(string relativeHostname, string zoneName)
    {
        var normalizedZone = zoneName.Trim().Trim('.').ToLowerInvariant();
        var normalizedRelative = relativeHostname.Trim().Trim('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalizedRelative)
            ? normalizedZone
            : $"{normalizedRelative}.{normalizedZone}";
    }
}
