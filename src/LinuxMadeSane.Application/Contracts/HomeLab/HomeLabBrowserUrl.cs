// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using System.Text.RegularExpressions;

namespace LinuxMadeSane.Application.Contracts.HomeLab;

public static partial class HomeLabBrowserUrl
{
    public const string ConfigurationKey = "lms-browser-entry";
    public const string DiscoveredKey = "lms-discovered-browser-entry";
    public const string Label = "com.linuxmadesane.browser-url";
    public static IReadOnlyList<string> SupportedLabels { get; } = [Label, "net.unraid.docker.webui", "homepage.href"];

    public static string? Resolve(string origin, string? configuredEntry)
    {
        if (string.IsNullOrWhiteSpace(configuredEntry) || !Uri.TryCreate(origin, UriKind.Absolute, out var server) ||
            server.Scheme is not ("http" or "https")) return null;
        var entry = configuredEntry.Trim();
        // Unraid container metadata may include host/port placeholders. Resolve
        // them against the selected reachable LMS or published endpoint.
        entry = entry.Replace("[IP]", server.Host, StringComparison.OrdinalIgnoreCase);
        entry = PortPlaceholder().Replace(entry, server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (entry.StartsWith("//", StringComparison.Ordinal) || entry.Contains('\\') || entry.Any(char.IsControl)) return null;
        if (Uri.TryCreate(entry, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            if (!string.IsNullOrEmpty(absolute.UserInfo)) return null;
            // Browser metadata supplies an entry point, not a replacement for
            // the LAN/Edge Gateway audience selected by the current session.
            entry = absolute.PathAndQuery + absolute.Fragment;
        }
        else if (!entry.StartsWith('/') || entry.Contains("://", StringComparison.Ordinal)) return null;
        var basePath = server.AbsolutePath.TrimEnd('/');
        var includesBase = basePath.Length > 0 && (entry.Equals(basePath, StringComparison.Ordinal) ||
            entry.StartsWith(basePath + "/", StringComparison.Ordinal) || entry.StartsWith(basePath + "?", StringComparison.Ordinal) || entry.StartsWith(basePath + "#", StringComparison.Ordinal));
        var baseUri = includesBase ? new Uri(server.GetLeftPart(UriPartial.Authority) + "/") : new Uri(server.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        return new Uri(baseUri, entry.TrimStart('/')).AbsoluteUri;
    }

    public static bool IsValid(string? entry) => Resolve("https://validation.invalid/", entry) is not null;

    [GeneratedRegex(@"\[PORT(?::\d+)?\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PortPlaceholder();
}
