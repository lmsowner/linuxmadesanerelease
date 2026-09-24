// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace LinuxMadeSane.Web.Services;

public static partial class CommunityReleaseRetention
{
    public static int Trim(
        string applicationDirectory,
        string contentRoot,
        IConfiguration configuration,
        ILogger logger)
    {
        var application = new DirectoryInfo(ResolvePath(applicationDirectory));
        var releases = application.Parent;
        if (releases?.Name != "releases" || releases.Parent is null || releases.LinkTarget is not null ||
            !File.Exists(Path.Combine(application.FullName, "edition.txt")) ||
            File.ReadAllText(Path.Combine(application.FullName, "edition.txt")).Trim() != "ce")
        {
            return 0;
        }

        var current = Path.Combine(releases.Parent.FullName, "current");
        if (!Directory.Exists(current) || new DirectoryInfo(current).LinkTarget is null ||
            ResolvePath(current) != application.FullName || !TryGetInstalledAt(application, out var activeInstalledAt))
        {
            return 0;
        }

        var protectedPaths = GetProtectedPaths(configuration, contentRoot);
        var priorReleases = releases.EnumerateDirectories()
            .Where(directory => directory.FullName != application.FullName && directory.LinkTarget is null)
            .Select(directory => (Directory: directory, InstalledAt: TryGetInstalledAt(directory, out var installedAt) ? installedAt : (DateTime?)null))
            // A newer directory may still be receiving an update. Its installer owns it.
            .Where(release => release.InstalledAt is not null && release.InstalledAt <= activeInstalledAt)
            .OrderByDescending(release => release.InstalledAt)
            .ThenByDescending(release => release.Directory.Name, StringComparer.Ordinal)
            .ToArray();

        var removed = 0;
        // The active installation and the newest previous installation are the two retained copies.
        foreach (var release in priorReleases.Skip(1))
        {
            if (ResolvePath(current) != application.FullName)
            {
                break;
            }

            var path = release.Directory.FullName;
            if (protectedPaths.Any(protectedPath => IsWithin(protectedPath, path)))
            {
                logger.LogWarning("Keeping old CE release {Release} because it contains configured persistent data or an active mount.", path);
                continue;
            }

            try
            {
                release.Directory.Refresh();
                if (release.Directory.LinkTarget is not null)
                {
                    continue;
                }

                release.Directory.Delete(recursive: true);
                removed++;
                logger.LogInformation("Removed old CE release {Release}; retaining the active release and one rollback copy.", path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not remove old CE release {Release}.", path);
            }
        }

        return removed;
    }

    private static bool TryGetInstalledAt(DirectoryInfo directory, out DateTime installedAt)
    {
        installedAt = default;
        var match = ReleaseDirectoryPattern().Match(directory.Name);
        var versionPath = Path.Combine(directory.FullName, "version.txt");
        return match.Success &&
               File.Exists(Path.Combine(directory.FullName, "LinuxMadeSane.Web")) &&
               File.Exists(versionPath) &&
               File.ReadAllText(versionPath).Trim() == match.Groups["version"].Value &&
               DateTime.TryParseExact(match.Groups["installed"].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out installedAt);
    }

    private static string[] GetProtectedPaths(IConfiguration configuration, string contentRoot)
    {
        var connection = new SqliteConnectionStringBuilder(
            configuration.GetConnectionString("LinuxMadeSane") ?? "Data Source=data/linuxmadesane.db");
        var paths = new List<string>
        {
            configuration["DataProtection:KeyDirectory"] ?? Path.Combine(contentRoot, "data", "protection-keys")
        };
        if (connection.Mode != SqliteOpenMode.Memory && connection.DataSource is not ("" or ":memory:"))
        {
            paths.Add(connection.DataSource);
        }

        var protectedPaths = paths.Select(path => ResolvePath(Path.GetFullPath(path, contentRoot))).ToList();
        // Mountinfo already reports resolved paths, including mounts we cannot access ourselves.
        // Old application folders can still contain mounted shares. Never traverse those during removal.
        if (OperatingSystem.IsLinux())
        {
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var fields = line.Split(' ');
                if (fields.Length > 4)
                {
                    protectedPaths.Add(Regex.Replace(fields[4], @"\\([0-7]{3})",
                        match => ((char)Convert.ToInt32(match.Groups[1].Value, 8)).ToString()));
                }
            }
        }

        return protectedPaths.ToArray();
    }

    private static bool IsWithin(string path, string directory) =>
        path.Equals(directory, StringComparison.Ordinal) ||
        path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string ResolvePath(string path)
    {
        var absolute = Path.GetFullPath(path);
        var resolved = Path.GetPathRoot(absolute)!;
        foreach (var segment in absolute[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, segment);
            FileSystemInfo entry = Directory.Exists(resolved) ? new DirectoryInfo(resolved) : new FileInfo(resolved);
            if (entry.LinkTarget is not null)
            {
                resolved = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? resolved;
            }
        }

        return Path.TrimEndingDirectorySeparator(resolved);
    }

    [GeneratedRegex(@"^(?<version>v\d{4}(?:\.\d{2}){4})-(?<installed>\d{14})(?:-\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseDirectoryPattern();
}
