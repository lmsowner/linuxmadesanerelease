namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareTunnelConfiguration(
    IReadOnlyList<CloudflareTunnelRoute> Routes);
