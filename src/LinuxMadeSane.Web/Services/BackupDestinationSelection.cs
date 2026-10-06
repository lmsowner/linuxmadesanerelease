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

    private static void ValidateAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.Trim('/') == "" ||
            path.Split('/').Any(part => part is "." or "..") || path.IndexOfAny(['\0', '\n', '\r']) >= 0)
            throw new InvalidOperationException("Choose a dedicated backup folder, not /. Enter its full path, for example /var/backups/lms.");
    }
}
