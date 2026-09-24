// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Reflection;

namespace LinuxMadeSane.Core.Versioning;

public static class LinuxMadeSaneBuildVersion
{
    public static string GetCurrent(Assembly? preferredAssembly = null, Assembly? fallbackAssembly = null, string? baseDirectory = null) =>
        ResolveCurrent(preferredAssembly, fallbackAssembly, baseDirectory ?? AppContext.BaseDirectory);

    private static string ResolveCurrent(Assembly? preferredAssembly, Assembly? fallbackAssembly, string baseDirectory)
    {
        var versionFile = ResolveVersionFile(baseDirectory);
        if (!string.IsNullOrWhiteSpace(versionFile))
        {
            return versionFile;
        }

        var assemblyVersion = Resolve(preferredAssembly) ?? Resolve(fallbackAssembly);
        return ResolveLatestReleaseForZeroRevision(assemblyVersion, baseDirectory) ??
               assemblyVersion ??
               "dev";
    }

    private static string? ResolveVersionFile(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return null;
        }

        var directory = Path.GetFullPath(baseDirectory);
        var candidates = new[]
        {
            Path.Combine(directory, "version.txt"),
            Path.Combine(directory, "app", "version.txt"),
            Directory.GetParent(directory) is { } parent
                ? Path.Combine(parent.FullName, "version.txt")
                : string.Empty
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
            {
                continue;
            }

            var version = File.ReadLines(candidate)
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))
                ?.Trim();
            if (IsProductVersion(version))
            {
                return version;
            }
        }

        return null;
    }

    private static string? Resolve(Assembly? assembly)
    {
        if (assembly is null)
        {
            return null;
        }

        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        }

        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrWhiteSpace(fileVersion))
        {
            return fileVersion.Trim();
        }

        return assembly.GetName().Version?.ToString();
    }

    private static string? ResolveLatestReleaseForZeroRevision(string? version, string baseDirectory)
    {
        var parsed = ParseLegacyProductVersion(version);
        if (parsed is null || parsed.Value.Revision != 0)
        {
            return null;
        }

        var maxRevision = -1;
        foreach (var packageDirectory in FindPackageDirectories(baseDirectory))
        {
            foreach (var candidate in Directory.EnumerateFileSystemEntries(packageDirectory, $"*{parsed.Value.Date}.*"))
            {
                var candidateVersion = ExtractProductVersion(Path.GetFileName(candidate), parsed.Value.Date);
                if (candidateVersion is null)
                {
                    continue;
                }

                maxRevision = Math.Max(maxRevision, candidateVersion.Value.Revision);
            }
        }

        return maxRevision > 0
            ? $"{parsed.Value.Date}.{maxRevision}"
            : null;
    }

    private static IEnumerable<string> FindPackageDirectories(string baseDirectory)
    {
        foreach (var seed in new[] { baseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = Directory.Exists(seed)
                ? new DirectoryInfo(Path.GetFullPath(seed))
                : Directory.GetParent(Path.GetFullPath(seed));

            while (directory is not null)
            {
                var packageDirectory = Path.Combine(directory.FullName, "artifacts", "packages");
                if (Directory.Exists(packageDirectory))
                {
                    yield return packageDirectory;
                    break;
                }

                directory = directory.Parent;
            }
        }
    }

    private static (string Date, int Revision)? ExtractProductVersion(string fileName, string date)
    {
        var dateIndex = fileName.IndexOf(date, StringComparison.Ordinal);
        if (dateIndex < 0)
        {
            return null;
        }

        var startIndex = dateIndex + date.Length;
        if (startIndex >= fileName.Length || fileName[startIndex] != '.')
        {
            return null;
        }

        startIndex++;
        var endIndex = startIndex;
        while (endIndex < fileName.Length && char.IsDigit(fileName[endIndex]))
        {
            endIndex++;
        }

        return endIndex > startIndex &&
               int.TryParse(fileName[startIndex..endIndex], out var revision)
            ? (date, revision)
            : null;
    }

    private static bool IsProductVersion(string? version)
        => IsTimestampProductVersion(version) || ParseLegacyProductVersion(version) is not null;

    private static bool IsTimestampProductVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var normalized = version.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 5 && parts.All(static part => int.TryParse(part, out var value) && value >= 0);
    }

    private static (string Date, int Revision)? ParseLegacyProductVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var parts = version.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4 ||
            parts.Any(part => !int.TryParse(part, out var value) || value < 0))
        {
            return null;
        }

        return ($"{parts[0]}.{parts[1]}.{parts[2]}", int.Parse(parts[3]));
    }
}
