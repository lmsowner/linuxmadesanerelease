// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public static class SshAuthorizedKeyInstallCommandBuilder
{
    public static string Build(string publicKey)
    {
        var normalizedKey = NormalizeSingleLine(publicKey);
        var quotedKey = QuoteShellSingle(normalizedKey);
        return $"public_key={quotedKey}; auth_dir=\"$HOME/.ssh\"; auth_file=\"$auth_dir/authorized_keys\"; umask 077; mkdir -p \"$auth_dir\" && touch \"$auth_file\" && chmod 700 \"$auth_dir\" && chmod 600 \"$auth_file\" && if grep -qxF \"$public_key\" \"$auth_file\"; then echo \"LMS public key already installed\"; else printf '%s\\n' \"$public_key\" >> \"$auth_file\" && echo \"LMS public key installed\"; fi";
    }

    private static string NormalizeSingleLine(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string QuoteShellSingle(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
