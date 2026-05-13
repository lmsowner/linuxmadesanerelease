using System.Net;
using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.EdgeGateway;
using LinuxMadeSane.Application.Services;

namespace LinuxMadeSane.Application.Services.EdgeGateway;

public static partial class EdgeGatewayRouteValidator
{
    public static string NormalizeHostname(string value)
    {
        var normalized = StripScheme(value).Trim().TrimEnd('.').ToLowerInvariant();
        if (!IsValidHostname(normalized))
        {
            throw new InvalidOperationException("Enter a valid hostname such as nas.example.com.");
        }

        return normalized;
    }

    public static string NormalizeDomainName(string value)
    {
        var normalized = NormalizeHostname(value);
        if (normalized.Count(static character => character == '.') < 1)
        {
            throw new InvalidOperationException("Enter a real public domain such as example.com.");
        }

        return normalized;
    }

    public static string NormalizeTargetHost(string value)
    {
        var normalized = StripScheme(value).Trim().TrimEnd('/').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Contains('/') ||
            normalized.Contains('\\') ||
            normalized.Contains(' ') ||
            normalized.Contains(':') && !IPAddress.TryParse(normalized, out _))
        {
            throw new InvalidOperationException("Enter only the backend host, IP address, or localhost. Put the scheme and port in their own fields.");
        }

        if (normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(normalized, out _) ||
            IsValidHostname(normalized))
        {
            return normalized;
        }

        throw new InvalidOperationException("Enter a valid backend host, IP address, or localhost.");
    }

    public static int NormalizeTargetPort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Enter a backend port between 1 and 65535.");
        }

        return port;
    }

    public static string NormalizePathPrefix(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "/")
        {
            return string.Empty;
        }

        if (!normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Contains('\\') ||
            normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Target path prefix must start with / and cannot contain traversal segments.");
        }

        return normalized.TrimEnd('/');
    }

    public static void ValidateRoute(EdgeGatewayRoute route)
    {
        var hostname = NormalizeHostname(route.Hostname);
        var domainName = NormalizeDomainName(route.DomainName);
        if (!hostname.Equals(domainName, StringComparison.OrdinalIgnoreCase) &&
            !hostname.EndsWith($".{domainName}", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The route hostname must sit inside the selected domain.");
        }

        _ = NormalizeTargetHost(route.TargetHost);
        _ = NormalizeTargetPort(route.TargetPort);
        _ = NormalizePathPrefix(route.TargetPathPrefix);

        if (!Enum.IsDefined(route.TargetScheme))
        {
            throw new InvalidOperationException("Choose http or https for the backend target.");
        }

        if (!Enum.IsDefined(route.AuthMode))
        {
            throw new InvalidOperationException("Choose a valid authentication policy.");
        }

        foreach (var entry in SplitList(route.AllowKnownIps))
        {
            if (!TrustedNetworkMatcher.IsValidAddressOrCidr(entry))
            {
                throw new InvalidOperationException($"Allowed known IP entry '{entry}' is not a valid IP address or CIDR.");
            }
        }
    }

    public static string BuildTargetUrl(EdgeGatewayRoute route)
    {
        var scheme = route.TargetScheme == EdgeGatewayTargetScheme.Https ? "https" : "http";
        return $"{scheme}://{NormalizeTargetHost(route.TargetHost)}:{NormalizeTargetPort(route.TargetPort)}";
    }

    public static IReadOnlyList<string> SplitList(string? value) =>
        (value ?? string.Empty)
        .Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(static item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string StripScheme(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[7..];
        }
        else if (normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[8..];
        }

        if (normalized.Contains('/') || normalized.Contains('\\'))
        {
            throw new InvalidOperationException("Enter only the hostname, without a URL path.");
        }

        return normalized;
    }

    private static bool IsValidHostname(string hostname)
    {
        if (hostname.Length is < 4 or > 253 ||
            hostname.Contains("..", StringComparison.Ordinal) ||
            hostname.StartsWith(".", StringComparison.Ordinal) ||
            hostname.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var labels = hostname.Split('.');
        if (labels.Length < 2)
        {
            return false;
        }

        return labels.All(static label =>
            label.Length is > 0 and <= 63 &&
            !label.StartsWith("-", StringComparison.Ordinal) &&
            !label.EndsWith("-", StringComparison.Ordinal) &&
            HostnameLabelRegex().IsMatch(label));
    }

    [GeneratedRegex("^[a-z0-9-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HostnameLabelRegex();
}
