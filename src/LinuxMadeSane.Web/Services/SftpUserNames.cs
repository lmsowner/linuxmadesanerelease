// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Web.Services;

public static class SftpUserNames
{
    public static string Suggest(string hostName, IEnumerable<string> existingNames)
    {
        var prefix = new string(hostName.Split('.')[0].ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray()).Trim('-');
        if (string.IsNullOrEmpty(prefix)) prefix = "server";
        if (!char.IsAsciiLetter(prefix[0]) && prefix[0] != '_') prefix = "host-" + prefix;
        prefix = prefix[..Math.Min(prefix.Length, 20)].TrimEnd('-');
        var used = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var number = 1; ; number++)
        {
            var candidate = $"{prefix}-sftp{number}";
            if (!used.Contains(candidate)) return candidate;
        }
    }
}
