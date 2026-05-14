namespace LinuxMadeSane.Core.Models.Shares;

public sealed record SshfsMountRequest(
    Guid HostId,
    string RemotePath,
    string LocalMountPath,
    bool PersistOnServer);
