// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.RegularExpressions;

namespace LinuxMadeSane.Infrastructure.Services.ConfigurationSummary;

internal static partial class SummaryValueFormatter
{
    public static string Words(string value) =>
        PascalBoundary().Replace(value, " $1").Trim();

    public static string Title(string value)
    {
        var normalized = Words(value);
        return string.IsNullOrWhiteSpace(normalized)
            ? "Not configured"
            : char.ToUpperInvariant(normalized[0]) + normalized[1..].ToLowerInvariant();
    }

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex PascalBoundary();
}
