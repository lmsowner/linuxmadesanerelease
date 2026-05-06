namespace LinuxMadeSane.Core.Models.Shares;

public sealed record RemoteShareMountResult(
    Guid? ManagedMountId,
    string RemoteUncPath,
    string LocalMountPath,
    bool Persisted,
    string StatusMessage);
