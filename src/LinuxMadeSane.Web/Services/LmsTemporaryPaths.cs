// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Web.Services;

internal static class LmsTemporaryPaths
{
    public static string Combine(params string[] segments)
    {
        var root = ResolveRoot();
        return segments.Length == 0
            ? root
            : Path.Combine([root, .. segments]);
    }

    private static string ResolveRoot()
    {
        var userName = SanitizeSegment(Environment.UserName);
        var root = Path.Combine(Path.GetTempPath(), $"linuxmadesane-{userName}");
        try
        {
            Directory.CreateDirectory(root);
            return root;
        }
        catch
        {
            var fallback = Path.Combine(Path.GetTempPath(), $"linuxmadesane-{Environment.ProcessId}");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private static string SanitizeSegment(string? value)
    {
        var source = string.IsNullOrWhiteSpace(value) ? "app" : value.Trim();
        var characters = source
            .Select(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-')
            .ToArray();
        var sanitized = new string(characters).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "app" : sanitized;
    }
}
