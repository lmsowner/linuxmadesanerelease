using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISftpChrootService
{
    SftpUserFolder BuildFolderLayout(string basePath, string userName);

    void ValidateFolderLayout(SftpUserFolder folder);
}
