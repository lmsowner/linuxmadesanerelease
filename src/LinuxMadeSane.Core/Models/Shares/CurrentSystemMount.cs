namespace LinuxMadeSane.Core.Models.Shares;

public sealed record CurrentSystemMount(
    string SourcePath,
    string LocalMountPath,
    string FileSystemType,
    string MountOptions,
    bool IsReadOnly,
    bool IsNetworkMount,
    bool IsManagedByLms);
