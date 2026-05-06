using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Contracts;

public sealed record SftpBrowserViewModel(
    ManagedHost Host,
    string CurrentPath,
    IReadOnlyList<SftpItem> Items);
