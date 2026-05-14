namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record LocalHttpServiceDiscoveryRequest(
    bool IncludeLocalhost = true,
    bool IncludeLan = true,
    bool IncludeTailnet = false);
