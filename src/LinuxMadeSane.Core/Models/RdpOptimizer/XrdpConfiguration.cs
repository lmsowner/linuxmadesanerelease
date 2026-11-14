// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Text.RegularExpressions;
namespace LinuxMadeSane.Core.Models.RdpOptimizer;
public static class XrdpConfiguration
{
    public static int Port(string contents)
    {
        var globals = contents.Split("[Globals]", StringSplitOptions.None).LastOrDefault()?.Split('[', 2)[0] ?? "";
        var value = Regex.Match(globals, @"(?m)^port=([^\r\n]+)").Groups[1].Value.Trim();
        var number = int.TryParse(value, out var plain) ? plain : int.TryParse(Regex.Match(value, @":(\d+)$").Groups[1].Value, out var addressed) ? addressed : 0;
        if (number is < 1 or > 65535) throw new InvalidOperationException("XRDP has no supported TCP listener configured.");
        return number;
    }
}
