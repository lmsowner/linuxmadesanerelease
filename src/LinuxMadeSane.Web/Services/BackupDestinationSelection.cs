// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Web.Services;

public static class BackupDestinationSelection
{
    public static string Resolve(string destination, string folder, string localPath)
    {
        if (destination == "local")
        {
            ValidateAbsolute(localPath);
            return localPath.TrimEnd('/');
        }

        if (string.IsNullOrEmpty(destination))
            throw new InvalidOperationException("Choose where to keep your backups.");
        ValidateAbsolute(destination);
        if (string.IsNullOrWhiteSpace(folder) || folder.StartsWith('/') ||
            folder.Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or "..") ||
            folder.IndexOfAny(['\0', '\n', '\r', '\\']) >= 0)
            throw new InvalidOperationException("Enter a folder name inside the selected disk or share, for example lms-backups.");
        return destination.TrimEnd('/') + "/" + folder;
    }

    public static string WithHostSubfolder(string basePath, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Contains('/') || folder.Contains('\\') || folder is "." or ".." || folder.IndexOfAny(['\0', '\n', '\r']) >= 0)
            throw new InvalidOperationException("Enter one subfolder name for this host and backup plan.");
        return Resolve(basePath, folder, "");
    }

    public static string SuggestHostSubfolder(string hostname, IEnumerable<string> existingPaths, int firstNumber)
    {
        var host = new string(hostname.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray()).Trim('.');
        if (host.Length == 0) host = "lms-host";
        var names = existingPaths.Select(path => Path.GetFileName(path.TrimEnd('/'))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var number = Math.Max(1, firstNumber);
        while (names.Contains($"{host}_{number}")) number++;
        return $"{host}_{number}";
    }

    private static void ValidateAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.Trim('/') == "" ||
            path.Split('/').Any(part => part is "." or "..") || path.IndexOfAny(['\0', '\n', '\r']) >= 0)
            throw new InvalidOperationException("Choose a dedicated backup folder, not /. Enter its full path, for example /var/backups/lms.");
    }
}
