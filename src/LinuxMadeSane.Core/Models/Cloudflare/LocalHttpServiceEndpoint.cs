// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace LinuxMadeSane.Core.Models.Cloudflare;

public enum DiscoveryExposure
{
    Publishable,
    InternalOnly,
    RequiresManualConfirmation,
    UnsafeToExpose
}

public sealed record LocalHttpServiceEndpoint(
    string Url,
    string Scheme,
    string Host,
    int Port,
    int StatusCode,
    string? Title,
    string? ServerHeader,
    string Scope = "Localhost",
    string? IpAddress = null,
    string? DisplayName = null,
    DateTimeOffset? DiscoveredAtUtc = null,
    string? FaviconDataUrl = null,
    int Confidence = 0,
    string ServiceName = "",
    string ServiceKind = "unknown",
    DiscoveryExposure Exposure = DiscoveryExposure.RequiresManualConfirmation,
    string Fingerprint = "",
    IReadOnlyList<string>? Evidence = null);

public static class LocalHttpServiceDiscoveryRanking
{
    private static readonly HashSet<string> SyntheticDiscoveryLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Known LAN neighbour",
        "LAN candidate",
        "Local LMS host"
    };

    public static int PresentationRank(LocalHttpServiceEndpoint endpoint)
    {
        var hasFavicon = !string.IsNullOrWhiteSpace(endpoint.FaviconDataUrl);
        var title = endpoint.Title?.Trim();
        var hasTitle = !string.IsNullOrWhiteSpace(title);
        if (IsHiddenFromPicker(endpoint))
        {
            return 4;
        }

        if (hasFavicon && hasTitle)
        {
            return 0;
        }

        if (hasFavicon)
        {
            return 1;
        }

        return hasTitle ? 2 : 3;
    }

    public static bool IsHiddenFromPicker(LocalHttpServiceEndpoint endpoint) =>
        endpoint.StatusCode is >= 400 and <= 599 ||
        !string.IsNullOrWhiteSpace(endpoint.Title) && LooksLikeErrorTitle(endpoint.Title);

    public static bool IsSyntheticDiscoveryLabel(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (SyntheticDiscoveryLabels.Contains(value.Trim()) || IsSubnetDiscoveryLabel(value.Trim()));

    public static string StableKey(LocalHttpServiceEndpoint endpoint)
    {
        var address = string.IsNullOrWhiteSpace(endpoint.IpAddress) ? endpoint.Host : endpoint.IpAddress;
        var identity = $"{address.Trim().TrimEnd('.').ToLowerInvariant()}|{endpoint.Port}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    public static string PickerLabel(LocalHttpServiceEndpoint endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint.Title) && !LooksLikeErrorTitle(endpoint.Title))
        {
            return endpoint.Title.Trim();
        }

        if (!string.IsNullOrWhiteSpace(endpoint.DisplayName) &&
            !IsSyntheticDiscoveryLabel(endpoint.DisplayName))
        {
            return endpoint.DisplayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(endpoint.Host))
        {
            return endpoint.Host.Trim();
        }

        if (!string.IsNullOrWhiteSpace(endpoint.IpAddress))
        {
            return endpoint.IpAddress.Trim();
        }

        return endpoint.Url;
    }

    public static int TitleQualityRank(string? title) =>
        string.IsNullOrWhiteSpace(title) || LooksLikeErrorTitle(title) ? 2 : 0;

    public static bool LooksLikeErrorTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return true;
        }

        var text = title.Trim();
        return text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("forbidden", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("bad gateway", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("bad request", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("internal server", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("service unavailable", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
               LooksLikeHttpStatusTitle(text);
    }

    private static bool LooksLikeHttpStatusTitle(string title)
    {
        var text = title.AsSpan().Trim();
        if (text.Length is < 3 or > 64)
        {
            return false;
        }

        for (var index = 0; index <= text.Length - 3; index++)
        {
            if (!char.IsDigit(text[index]) || !char.IsDigit(text[index + 1]) || !char.IsDigit(text[index + 2]) ||
                index > 0 && char.IsDigit(text[index - 1]) ||
                index + 3 < text.Length && char.IsDigit(text[index + 3]))
            {
                continue;
            }

            var code = (text[index] - '0') * 100 + (text[index + 1] - '0') * 10 + text[index + 2] - '0';
            if (code is >= 300 and <= 599)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsErrorOrRedirectStatusCode(int statusCode) =>
        statusCode is >= 300 and <= 399 or >= 400 and <= 599;

    public static bool IsUnknownLabel(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        IsSyntheticDiscoveryLabel(value) ||
        value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("unknown-http", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("unknown http service", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("unknown http", StringComparison.OrdinalIgnoreCase);

    private static bool IsSubnetDiscoveryLabel(string value)
    {
        var separator = value.IndexOf(' ');
        if (separator <= 0)
        {
            return false;
        }

        var network = value[..separator];
        var slash = network.LastIndexOf('/');
        if (slash <= 0 ||
            !IPAddress.TryParse(network[..slash], out _) ||
            !int.TryParse(network[(slash + 1)..], out var prefixLength) ||
            prefixLength is < 0 or > 128)
        {
            return false;
        }

        var suffix = value[(separator + 1)..].Trim();
        return suffix.Equals("known neighbour", StringComparison.OrdinalIgnoreCase) ||
               suffix.Equals("known neighbours", StringComparison.OrdinalIgnoreCase) ||
               suffix.StartsWith("via ", StringComparison.OrdinalIgnoreCase);
    }
}
