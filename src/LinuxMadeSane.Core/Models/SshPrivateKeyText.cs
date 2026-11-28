// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Buffers.Text;
using System.Text;
using System.Text.RegularExpressions;

namespace LinuxMadeSane.Core.Models;

public static partial class SshPrivateKeyText
{
    private const string Dash = @"[\p{Pd}\u2212]+";
    private const string Kind = @"(?<kind>(?:(?:OPENSSH|RSA|EC|DSA|ENCRYPTED) )?PRIVATE KEY)";

    [GeneratedRegex(@"\A" + Dash + @"\s*BEGIN " + Kind + @"\s*" + Dash, RegexOptions.NonBacktracking)]
    private static partial Regex BeginMarker();

    [GeneratedRegex(Dash + @"\s*END " + Kind + @"\s*" + Dash + @"\z", RegexOptions.NonBacktracking)]
    private static partial Regex EndMarker();

    // A null result leaves other formats supported by the SSH reader, such as PuTTY, to that reader.
    public static string? Normalize(string text)
    {
        text = text.Replace("\uFEFF", "").Replace("\u200B", "")
            .Replace(@"\r\n", "\n").Replace(@"\n", "\n").Replace(@"\r", "\n").ReplaceLineEndings("\n").Trim();
        var begin = BeginMarker().Match(text);
        if (!begin.Success) return null;
        var end = EndMarker().Match(text);
        var kind = begin.Groups["kind"].Value;
        if (!end.Success || end.Index <= begin.Length || end.Groups["kind"].Value != kind) throw IncompleteKey();

        var body = text[begin.Length..end.Index].Trim();
        var result = new StringBuilder(text.Length).Append("-----BEGIN ").Append(kind).Append("-----\n");
        // Traditional encrypted PEM includes text headers before the base64 payload.
        bool hasHeaders = false;
        while (body.StartsWith("Proc-Type:", StringComparison.Ordinal) || body.StartsWith("DEK-Info:", StringComparison.Ordinal))
        {
            var newline = body.IndexOf('\n');
            if (newline < 0) throw IncompleteKey();
            hasHeaders = true;
            result.Append(body[..newline].Trim()).Append('\n');
            body = body[(newline + 1)..].TrimStart();
        }
        if (hasHeaders) result.Append('\n');

        var payload = string.Concat(body.Where(c => !char.IsWhiteSpace(c)));
        if (payload.Length == 0 || !Base64.IsValid(payload)) throw IncompleteKey();
        var width = kind == "OPENSSH PRIVATE KEY" ? 70 : 64;
        for (var i = 0; i < payload.Length; i += width)
            result.Append(payload, i, Math.Min(width, payload.Length - i)).Append('\n');
        return result.Append("-----END ").Append(kind).Append("-----\n").ToString();
    }

    private static FormatException IncompleteKey() => new(
        "Check that you copied one complete private key, including its matching BEGIN and END lines and all the text between them.");
}
