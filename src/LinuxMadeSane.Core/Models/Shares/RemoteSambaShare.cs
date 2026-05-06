namespace LinuxMadeSane.Core.Models.Shares;

public sealed record RemoteSambaShare(
    string Name,
    string ShareType,
    string Comment,
    bool IsMountable,
    bool IsSpecial);
