// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Monitoring;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalDriveUsageService : ILocalDriveUsageService
{
    public const string PrivilegedScanCommand = "--drive-usage-scan";

    private const int MaximumScanDepth = 512;
    private const string DefaultMountInfoPath = "/proc/self/mountinfo";
    private static readonly TimeSpan PrivilegedScanTimeout = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> ExcludedFileSystemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "9p", "afs", "autofs", "binfmt_misc", "bpf", "ceph", "cgroup", "cgroup2", "cifs",
        "configfs", "davfs", "davfs2", "debugfs", "devpts", "devtmpfs", "efivarfs", "fuse.ceph",
        "fuse.davfs", "fuse.gcsfuse", "fuse.glusterfs", "fuse.goofys", "fuse.httpdirfs", "fuse.rclone",
        "fuse.s3fs", "fuse.smbnetfs", "fuse.sshfs", "fusectl", "glusterfs", "gpfs", "hugetlbfs",
        "lustre", "mqueue", "ncpfs", "nfs", "nfs4", "orangefs", "panfs", "proc", "pstore", "ramfs",
        "securityfs", "smb3", "sysfs", "tmpfs", "tracefs"
    };

    private readonly ILinuxCommandRunner? commandRunner;
    private readonly Func<bool> isRunningAsRoot;
    private readonly string mountInfoPath;
    private readonly SemaphoreSlim scanGate = new(1, 1);

    public LocalDriveUsageService(ILinuxCommandRunner? commandRunner = null)
        : this(commandRunner, DefaultMountInfoPath, IsCurrentProcessRoot)
    {
    }

    internal LocalDriveUsageService(string mountInfoPath)
        : this(null, mountInfoPath, IsCurrentProcessRoot)
    {
    }

    internal LocalDriveUsageService(
        ILinuxCommandRunner? commandRunner,
        string mountInfoPath,
        Func<bool> isRunningAsRoot)
    {
        this.commandRunner = commandRunner;
        this.mountInfoPath = mountInfoPath;
        this.isRunningAsRoot = isRunningAsRoot;
    }

    public async Task<LocalDriveUsageSnapshot> ScanAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            throw new ArgumentException("Drive usage paths must be absolute.", nameof(path));
        }

        var normalizedPath = NormalizePath(path);

        await scanGate.WaitAsync(cancellationToken);
        try
        {
            if (commandRunner is not null && OperatingSystem.IsLinux() && !isRunningAsRoot())
            {
                var privilegedSnapshot = await TryScanWithPrivilegeAsync(normalizedPath, cancellationToken);
                if (privilegedSnapshot is not null)
                {
                    return privilegedSnapshot;
                }

                return await Task.Run(
                    () => Scan(
                        normalizedPath,
                        cancellationToken,
                        accessWasPrivileged: false,
                        "The privileged drive scan is unavailable. Results use the LMS service account and may be incomplete."),
                    cancellationToken);
            }

            return await Task.Run(
                () => Scan(normalizedPath, cancellationToken, isRunningAsRoot(), accessWarning: null),
                cancellationToken);
        }
        finally
        {
            scanGate.Release();
        }
    }

    private async Task<LocalDriveUsageSnapshot?> TryScanWithPrivilegeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var launch = ResolvePrivilegedScanLaunch();
        if (launch is null)
        {
            return null;
        }

        var arguments = launch.PrefixArguments
            .Concat([PrivilegedScanCommand, path])
            .ToArray();
        var result = await commandRunner!.RunAsync(
            new LinuxCommandRequest(
                launch.FileName,
                arguments,
                RequiresSudo: true,
                PrivilegedScanTimeout,
                "Read local drive usage with elevated filesystem access"),
            dryRun: false,
            cancellationToken);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<LocalDriveUsageSnapshot>(result.StandardOutput, JsonOptions);
            return snapshot is { AccessWasPrivileged: true } &&
                   string.Equals(snapshot.Path, path, StringComparison.Ordinal)
                ? snapshot
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private LocalDriveUsageSnapshot Scan(
        string path,
        CancellationToken cancellationToken,
        bool accessWasPrivileged,
        string? accessWarning)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"The folder '{path}' does not exist or cannot be accessed.");
        }

        if (IsSymbolicLink(new DirectoryInfo(path)))
        {
            throw new InvalidOperationException("Drive usage scans cannot follow a symbolic link.");
        }

        var excludedMountPoints = ReadExcludedMountPoints(path);
        var scan = ScanDirectory(path, excludedMountPoints, cancellationToken, 0, includeChildren: true);
        var totalBytes = scan.Entries.Sum(entry => entry.UsedBytes);
        var items = scan.Entries
            .Select(entry => new LocalDriveUsageItem(
                entry.Name,
                entry.FullPath,
                entry.IsDirectory,
                entry.UsedBytes,
                totalBytes <= 0 ? 0 : entry.UsedBytes / (double)totalBytes * 100d,
                entry.FileCount,
                entry.DirectoryCount,
                entry.ModifiedAtUtc,
                entry.IsPartial))
            .OrderByDescending(item => item.IsDirectory)
            .ThenByDescending(item => item.UsedBytes)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var fileSystem = ReadFileSystemMetric(path);
        return new LocalDriveUsageSnapshot(
            path,
            GetParentPath(path),
            DateTimeOffset.UtcNow,
            totalBytes,
            scan.FileCount,
            scan.DirectoryCount,
            scan.SkippedItemCount,
            scan.IsPartial,
            fileSystem,
            items)
        {
            AccessWasPrivileged = accessWasPrivileged,
            AccessWarning = accessWarning
        };
    }

    private static PrivilegedScanLaunch? ResolvePrivilegedScanLaunch()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !Path.IsPathRooted(processPath))
        {
            return null;
        }

        if (!string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return new PrivilegedScanLaunch(processPath, []);
        }

        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        return string.IsNullOrWhiteSpace(entryAssemblyPath) || !Path.IsPathRooted(entryAssemblyPath)
            ? null
            : new PrivilegedScanLaunch(processPath, [entryAssemblyPath]);
    }

    private static bool IsCurrentProcessRoot()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        return GetEffectiveUserId() == 0;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    private static DirectoryScanResult ScanDirectory(
        string path,
        IReadOnlySet<string> excludedMountPoints,
        CancellationToken cancellationToken,
        int depth,
        bool includeChildren)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > MaximumScanDepth)
        {
            return DirectoryScanResult.Partial();
        }

        var entries = includeChildren ? new List<MeasuredEntry>() : null;
        long totalBytes = 0;
        long fileCount = 0;
        long directoryCount = 0;
        long skippedItemCount = 0;
        var isPartial = false;
        IEnumerable<FileSystemInfo> children;

        try
        {
            children = new DirectoryInfo(path).EnumerateFileSystemInfos();
        }
        catch (Exception exception) when (IsRecoverableFileSystemException(exception))
        {
            return DirectoryScanResult.Partial();
        }

        try
        {
            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (IsSymbolicLink(child))
                    {
                        skippedItemCount++;
                        continue;
                    }

                    var childPath = NormalizePath(child.FullName);
                    if (excludedMountPoints.Contains(childPath))
                    {
                        skippedItemCount++;
                        continue;
                    }

                    if (child is DirectoryInfo)
                    {
                        var nested = ScanDirectory(
                            childPath,
                            excludedMountPoints,
                            cancellationToken,
                            depth + 1,
                            includeChildren: false);
                        directoryCount += 1 + nested.DirectoryCount;
                        fileCount += nested.FileCount;
                        totalBytes += nested.TotalBytes;
                        skippedItemCount += nested.SkippedItemCount;
                        isPartial |= nested.IsPartial;
                        entries?.Add(new MeasuredEntry(
                            child.Name,
                            childPath,
                            true,
                            nested.TotalBytes,
                            nested.FileCount,
                            nested.DirectoryCount,
                            ReadModifiedAtUtc(child),
                            nested.IsPartial));
                    }
                    else if (child is FileInfo file)
                    {
                        var length = Math.Max(0, file.Length);
                        totalBytes += length;
                        fileCount++;
                        entries?.Add(new MeasuredEntry(
                            child.Name,
                            childPath,
                            false,
                            length,
                            1,
                            0,
                            ReadModifiedAtUtc(child),
                            false));
                    }
                }
                catch (Exception exception) when (IsRecoverableFileSystemException(exception))
                {
                    skippedItemCount++;
                    isPartial = true;
                }
            }
        }
        catch (Exception exception) when (IsRecoverableFileSystemException(exception))
        {
            skippedItemCount++;
            isPartial = true;
        }

        return new DirectoryScanResult(
            totalBytes,
            fileCount,
            directoryCount,
            skippedItemCount,
            isPartial,
            entries ?? []);
    }

    private IReadOnlySet<string> ReadExcludedMountPoints(string scanRoot)
    {
        if (!File.Exists(mountInfoPath))
        {
            throw new IOException("The Linux mount table is unavailable, so LMS cannot safely exclude network filesystems from this scan.");
        }

        return ParseExcludedMountPoints(File.ReadLines(mountInfoPath), scanRoot);
    }

    internal static IReadOnlySet<string> ParseExcludedMountPoints(IEnumerable<string> lines, string scanRoot)
    {
        var normalizedRoot = NormalizePath(scanRoot);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var before = line[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var after = line[(separator + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (before.Length < 5 || after.Length < 1 || !ExcludedFileSystemTypes.Contains(after[0]))
            {
                continue;
            }

            var decodedMountPoint = UnescapeMountInfoPath(before[4]);
            if (!Path.IsPathRooted(decodedMountPoint))
            {
                continue;
            }

            var mountPoint = NormalizePath(decodedMountPoint);
            if (!string.Equals(mountPoint, normalizedRoot, StringComparison.Ordinal) && IsPathWithin(mountPoint, normalizedRoot))
            {
                result.Add(mountPoint);
            }
        }

        return result;
    }

    internal static string UnescapeMountInfoPath(string value) => value
        .Replace("\\040", " ", StringComparison.Ordinal)
        .Replace("\\011", "\t", StringComparison.Ordinal)
        .Replace("\\012", "\n", StringComparison.Ordinal)
        .Replace("\\134", "\\", StringComparison.Ordinal);

    private static bool IsPathWithin(string path, string root)
    {
        var relativePath = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relativePath) &&
               !string.Equals(relativePath, "..", StringComparison.Ordinal) &&
               !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IsSymbolicLink(FileSystemInfo item)
    {
        try
        {
            return item.LinkTarget is not null || item.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (IsRecoverableFileSystemException(exception))
        {
            return true;
        }
    }

    private static DateTimeOffset? ReadModifiedAtUtc(FileSystemInfo item)
    {
        try
        {
            return item.LastWriteTimeUtc;
        }
        catch (Exception exception) when (IsRecoverableFileSystemException(exception))
        {
            return null;
        }
    }

    private static LocalDriveFileSystemMetric ReadFileSystemMetric(string path)
    {
        try
        {
            var drive = DriveInfo.GetDrives()
                .Where(candidate => candidate.IsReady && IsPathWithin(path, NormalizePath(candidate.RootDirectory.FullName)))
                .OrderByDescending(candidate => NormalizePath(candidate.RootDirectory.FullName).Length)
                .FirstOrDefault();
            if (drive is null)
            {
                return new LocalDriveFileSystemMetric("Unknown", "Unknown", 0, 0, 0);
            }

            var total = Math.Max(0, drive.TotalSize);
            var available = Math.Max(0, drive.AvailableFreeSpace);
            return new LocalDriveFileSystemMetric(
                drive.Name,
                string.IsNullOrWhiteSpace(drive.DriveFormat) ? "Unknown" : drive.DriveFormat,
                total,
                Math.Max(0, total - available),
                available);
        }
        catch (Exception exception) when (IsRecoverableFileSystemException(exception))
        {
            return new LocalDriveFileSystemMetric("Unknown", "Unknown", 0, 0, 0);
        }
    }

    private static string? GetParentPath(string path)
    {
        var root = Path.GetPathRoot(path);
        return string.Equals(path, root, StringComparison.Ordinal)
            ? null
            : Directory.GetParent(path)?.FullName;
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.Ordinal)
            ? fullPath
            : Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static bool IsRecoverableFileSystemException(Exception exception) =>
        exception is UnauthorizedAccessException or IOException;

    private sealed record MeasuredEntry(
        string Name,
        string FullPath,
        bool IsDirectory,
        long UsedBytes,
        long FileCount,
        long DirectoryCount,
        DateTimeOffset? ModifiedAtUtc,
        bool IsPartial);

    private sealed record DirectoryScanResult(
        long TotalBytes,
        long FileCount,
        long DirectoryCount,
        long SkippedItemCount,
        bool IsPartial,
        IReadOnlyList<MeasuredEntry> Entries)
    {
        public static DirectoryScanResult Partial() => new(0, 0, 0, 1, true, []);
    }

    private sealed record PrivilegedScanLaunch(
        string FileName,
        IReadOnlyList<string> PrefixArguments);
}
