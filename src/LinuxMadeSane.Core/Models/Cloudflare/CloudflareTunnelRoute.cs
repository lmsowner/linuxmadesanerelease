namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareTunnelRoute(
    string Hostname,
    string Service);
