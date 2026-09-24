// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LinuxMadeSane.Infrastructure.Services;

internal static partial class HomeLabPublicIpParser
{
    public static string? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var trimmed = output.Trim();
        try
        {
            if (JsonNode.Parse(trimmed)?["public_ip"]?.GetValue<string>() is { } jsonIp &&
                IPAddress.TryParse(jsonIp.Trim(), out var parsedJsonIp))
            {
                return parsedJsonIp.ToString();
            }
        }
        catch (JsonException)
        {
            // The fallback handles plain-text responses and command output containing JSON.
        }

        var jsonMatch = PublicIpJsonRegex().Match(trimmed);
        if (jsonMatch.Success && IPAddress.TryParse(jsonMatch.Groups["ip"].Value, out var embeddedJsonIp))
        {
            return embeddedJsonIp.ToString();
        }

        foreach (var token in trimmed.Split(['\r', '\n', ' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = token.Trim('"', '\'', '{', '}', '[', ']', '(', ')', ':');
            if (IPAddress.TryParse(candidate, out var parsedIp))
            {
                return parsedIp.ToString();
            }
        }

        return null;
    }

    public static string? ParseGluetunLogs(string? logs)
    {
        if (string.IsNullOrWhiteSpace(logs))
        {
            return null;
        }

        string? latest = null;
        foreach (Match match in GluetunPublicIpLogRegex().Matches(logs))
        {
            if (IPAddress.TryParse(match.Groups["ip"].Value, out var parsedIp))
            {
                latest = parsedIp.ToString();
            }
        }

        return latest;
    }

    [GeneratedRegex("\\\"public_ip\\\"\\s*:\\s*\\\"(?<ip>[^\\\"]+)\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex PublicIpJsonRegex();

    [GeneratedRegex("Public IP address is\\s+(?<ip>[0-9a-fA-F:.]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GluetunPublicIpLogRegex();
}
