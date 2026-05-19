// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Web.Components.Pages;

public static class ScheduledTaskPathSuggestions
{
    private static readonly string[] DefaultRoots =
    [
        "/srv",
        "/srv/shares",
        "/mnt",
        "/home",
        "/tmp",
        "/var"
    ];

    public static IReadOnlyList<string> BuildBaseSuggestions(string? query, string? relatedPath)
    {
        var suggestions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in DefaultRoots)
        {
            AddPathSuggestion(suggestions, root);
        }

        AddPathSuggestion(suggestions, relatedPath);
        AddPathSuggestion(suggestions, GetParentDirectory(relatedPath));

        var trimmedQuery = query?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(trimmedQuery))
        {
            var normalizedQuery = NormalizeSuggestedPath(trimmedQuery);
            AddPathSuggestion(suggestions, normalizedQuery);
            AddPathSuggestion(suggestions, GetParentDirectory(normalizedQuery));
        }

        return suggestions.ToArray();
    }

    public static IReadOnlyList<string> BuildInspectionPaths(string normalizedQuery, string? relatedPath)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(relatedPath))
        {
            paths.Add(NormalizeSuggestedPath(relatedPath));
            paths.Add(GetParentDirectory(relatedPath));
        }

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            paths.Add(normalizedQuery);
            paths.Add(GetParentDirectory(normalizedQuery));
        }

        if (paths.Count == 0)
        {
            foreach (var root in DefaultRoots)
            {
                paths.Add(root);
            }
        }

        return paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
    }

    public static IReadOnlyList<string> FilterAndOrderSuggestions(
        IEnumerable<string> suggestions,
        string normalizedQuery,
        string? currentValue)
    {
        var currentNormalizedValue = string.IsNullOrWhiteSpace(currentValue)
            ? null
            : NormalizeSuggestedPath(currentValue);

        var normalizedSuggestions = suggestions
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeSuggestedPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var filtered = string.IsNullOrWhiteSpace(normalizedQuery)
            ? normalizedSuggestions
            : normalizedSuggestions.Where(path =>
                path.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                GetPathLabel(path).StartsWith(normalizedQuery.TrimStart('/'), StringComparison.OrdinalIgnoreCase) ||
                path.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase));

        return filtered
            .Where(path => !string.Equals(path, currentNormalizedValue, StringComparison.Ordinal))
            .OrderBy(path => GetPathSuggestionRank(path, normalizedQuery))
            .ThenBy(GetPathLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToArray();
    }

    public static int GetPathSuggestionRank(string path, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return path is "/" or "/srv" or "/srv/shares" ? 0 : 1;
        }

        var label = GetPathLabel(path);
        if (path.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (label.StartsWith(query.TrimStart('/'), StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return path.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : 3;
    }

    public static string GetPathLabel(string path)
    {
        var normalizedPath = NormalizeSuggestedPath(path);
        if (normalizedPath == "/")
        {
            return "/";
        }

        var separator = normalizedPath.LastIndexOf('/');
        return separator >= 0 ? normalizedPath[(separator + 1)..] : normalizedPath;
    }

    public static string GetParentDirectory(string? path)
    {
        var normalizedPath = NormalizeSuggestedPath(path);
        if (normalizedPath == "/")
        {
            return "/";
        }

        var separator = normalizedPath.LastIndexOf('/');
        if (separator <= 0)
        {
            return "/";
        }

        return normalizedPath[..separator];
    }

    public static string NormalizeSuggestedPath(string? path)
    {
        var normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "/";
        }

        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = "/" + normalized.TrimStart('/');
        }

        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
    }

    private static void AddPathSuggestion(ISet<string> suggestions, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        suggestions.Add(NormalizeSuggestedPath(path));
    }
}
