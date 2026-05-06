using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class DefaultSftpChrootService : ISftpChrootService
{
    public SftpUserFolder BuildFolderLayout(string basePath, string userName)
    {
        var normalizedBasePath = NormalizePath(basePath);
        var normalizedUserName = (userName ?? string.Empty).Trim().ToLowerInvariant();
        var chrootPath = $"{normalizedBasePath}/{normalizedUserName}";
        var writablePath = $"{chrootPath}/files";

        return new SftpUserFolder(
            normalizedBasePath,
            chrootPath,
            writablePath,
            "root",
            "root",
            "755",
            normalizedUserName,
            SftpServerDefaults.ManagedUsersGroup,
            "750");
    }

    public void ValidateFolderLayout(SftpUserFolder folder)
    {
        if (string.IsNullOrWhiteSpace(folder.ChrootPath) ||
            string.IsNullOrWhiteSpace(folder.WritablePath))
        {
            throw new InvalidOperationException("The SFTP folder layout is incomplete.");
        }

        if (!folder.WritablePath.StartsWith($"{folder.ChrootPath}/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The writable SFTP directory must stay inside the user chroot.");
        }

        if (!string.Equals(folder.ChrootOwner, "root", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(folder.ChrootGroup, "root", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The chroot directory must be owned by root:root.");
        }
    }

    private static string NormalizePath(string path)
    {
        var normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return SftpServerDefaults.BasePath;
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
}
