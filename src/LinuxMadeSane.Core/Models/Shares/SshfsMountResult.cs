namespace LinuxMadeSane.Core.Models.Shares;

public sealed record SshfsMountResult(
    Guid? ManagedMountId,
    string RemoteSourcePath,
    string LocalMountPath,
    bool Persisted,
    string StatusMessage);
