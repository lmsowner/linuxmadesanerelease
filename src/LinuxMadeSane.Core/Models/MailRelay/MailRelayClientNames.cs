// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Text.RegularExpressions;
namespace LinuxMadeSane.Core.Models.MailRelay;
public static class MailRelayClientNames
{
    public static string Suggest(string name, IEnumerable<string> existing)
    {
        var basis = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (basis.Length == 0) basis = "smtp-user";
        if (basis.Length > 40) basis = basis[..40].TrimEnd('-');
        var used = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var value = basis;
        for (var number = 2; used.Contains(value); number++) value = basis + "-" + number;
        return value;
    }
}
