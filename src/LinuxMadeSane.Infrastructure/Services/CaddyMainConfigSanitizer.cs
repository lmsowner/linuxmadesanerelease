// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Services;

internal static class CaddyMainConfigSanitizer
{
    public static string RemovePackagedDefaultSite(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var output = new List<string>(lines.Length);

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (!line.Trim().Equals(":80 {", StringComparison.Ordinal))
            {
                output.Add(line);
                continue;
            }

            var block = new List<string> { line };
            var depth = CountBraceDelta(line);
            var hasDefaultRoot = false;
            var hasDefaultFileServer = false;

            while (index + 1 < lines.Length && depth > 0)
            {
                index++;
                line = lines[index];
                block.Add(line);

                var trimmed = line.Trim();
                hasDefaultRoot |= trimmed.Equals("root * /usr/share/caddy", StringComparison.Ordinal);
                hasDefaultFileServer |= trimmed.Equals("file_server", StringComparison.Ordinal);
                depth += CountBraceDelta(line);
            }

            if (!hasDefaultRoot || !hasDefaultFileServer)
            {
                output.AddRange(block);
            }
        }

        return string.Join('\n', output);
    }

    private static int CountBraceDelta(string line)
    {
        var delta = 0;
        foreach (var character in line)
        {
            delta += character switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0
            };
        }

        return delta;
    }
}
