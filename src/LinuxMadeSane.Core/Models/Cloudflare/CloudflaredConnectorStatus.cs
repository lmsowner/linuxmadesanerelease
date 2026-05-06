namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflaredConnectorStatus(
    bool IsInstalled,
    bool IsRunning,
    string? ServiceFilePath,
    string? TunnelId);
