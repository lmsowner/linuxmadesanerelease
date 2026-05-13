namespace LinuxMadeSane.Web.Services;

public static class ApplicationUpdateVersionComparer
{
    public static bool IsNewer(string? candidateVersion, string? currentVersion)
    {
        var candidate = Parse(candidateVersion);
        var current = Parse(currentVersion);
        if (candidate.Count == 0 || current.Count == 0)
        {
            return false;
        }

        var length = Math.Max(candidate.Count, current.Count);
        for (var index = 0; index < length; index++)
        {
            var left = index < candidate.Count ? candidate[index] : 0;
            var right = index < current.Count ? current[index] : 0;
            if (left > right)
            {
                return true;
            }

            if (left < right)
            {
                return false;
            }
        }

        return false;
    }

    private static IReadOnlyList<int> Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return [];
        }

        var normalized = version.Trim().Split('+', 2, StringSplitOptions.TrimEntries)[0];
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var parts = normalized.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return [];
        }

        var values = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var value) || value < 0)
            {
                return [];
            }

            values.Add(value);
        }

        return values;
    }
}
