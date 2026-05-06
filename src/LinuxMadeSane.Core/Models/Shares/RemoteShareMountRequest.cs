namespace LinuxMadeSane.Core.Models.Shares;

public sealed record RemoteShareMountRequest(
    string RemoteHost,
    string? RemoteAddress,
    string ShareName,
    string LocalMountPath,
    string? UserName,
    string? Password,
    string? Domain,
    bool PersistOnServer);
