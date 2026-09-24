// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public static class EdgeGatewayDefaultNamespace
{
    private const string RelaySuffix = "-relay";
    private const int MaxDnsLabelLength = 63;
    private static readonly int MaxMachineSlugLength = MaxDnsLabelLength - RelaySuffix.Length;

    public static string BuildForMachineName(string? machineName)
    {
        var firstLabel = (machineName ?? string.Empty)
            .Trim()
            .Split('.', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        var slug = BuildDnsLabelSlug(firstLabel);
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "lms";
        }

        if (slug.Length > MaxMachineSlugLength)
        {
            slug = slug[..MaxMachineSlugLength].Trim('-');
            if (string.IsNullOrWhiteSpace(slug))
            {
                slug = "lms";
            }
        }

        return $"{slug}{RelaySuffix}";
    }

    public static string ResolveConfiguredDefault(string? configuredGatewaySubdomain)
    {
        var configured = configuredGatewaySubdomain?.Trim() ?? string.Empty;
        return IsLegacyBuiltInDefault(configured)
            ? BuildForMachineName(Environment.MachineName)
            : configured;
    }

    private static bool IsLegacyBuiltInDefault(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return true;
        }

        var normalized = configured.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.StartsWith("*.", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.Equals("relay", StringComparison.Ordinal);
    }

    private static string BuildDnsLabelSlug(string value)
    {
        var result = new List<char>(value.Length);
        var previousWasHyphen = false;

        foreach (var character in value.ToLowerInvariant())
        {
            var next = char.IsLetterOrDigit(character) ? character : '-';
            if (next == '-' && previousWasHyphen)
            {
                continue;
            }

            result.Add(next);
            previousWasHyphen = next == '-';
        }

        return new string(result.ToArray()).Trim('-');
    }
}
