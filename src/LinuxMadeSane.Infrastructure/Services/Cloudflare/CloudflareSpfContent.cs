// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using System.Text;

namespace LinuxMadeSane.Infrastructure.Services.Cloudflare;

internal static class CloudflareSpfContent
{
    internal static bool IsSpf(string text) =>
        text.Equals("v=spf1", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("v=spf1 ", StringComparison.OrdinalIgnoreCase);

    // Cloudflare may return a TXT value as quoted DNS character-strings. A split
    // between strings contributes no space: only bytes inside the quotes count.
    internal static string Normalize(string type, string content)
    {
        if (!type.Equals("TXT", StringComparison.OrdinalIgnoreCase) || !content.StartsWith('"'))
            return content;

        var decoded = new StringBuilder();
        var position = 0;
        while (position < content.Length)
        {
            if (content[position++] != '"') return content;
            while (position < content.Length && content[position] != '"')
            {
                var character = content[position++];
                if (character == '\\' && position < content.Length)
                {
                    character = content[position++];
                    if (char.IsAsciiDigit(character) && position + 1 < content.Length &&
                        char.IsAsciiDigit(content[position]) && char.IsAsciiDigit(content[position + 1]) &&
                        byte.TryParse(content.AsSpan(position - 1, 3), out var value))
                    {
                        character = (char)value;
                        position += 2;
                    }
                }
                decoded.Append(character);
            }
            if (position == content.Length) return content;
            position++;
            while (position < content.Length && char.IsWhiteSpace(content[position])) position++;
        }

        var text = decoded.ToString();
        return IsSpf(text) ? text : content;
    }
}
