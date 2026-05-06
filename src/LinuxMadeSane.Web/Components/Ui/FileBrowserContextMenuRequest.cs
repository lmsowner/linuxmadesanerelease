using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Components.Ui;

public sealed record FileBrowserContextMenuRequest(
    double ClientX,
    double ClientY,
    string TargetPath,
    string ActionDirectoryPath,
    string DisplayName,
    bool IsDirectory,
    SftpItem? Item);
