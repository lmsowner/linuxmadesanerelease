namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareTunnelRoute
{
    public CloudflareTunnelRoute(string hostname, string service)
        : this(hostname, service, CloudflareOriginRequestSettings.Default)
    {
    }

    public CloudflareTunnelRoute(string hostname, string service, bool noTlsVerify)
        : this(hostname, service, CloudflareOriginRequestSettings.Default with { NoTlsVerify = noTlsVerify })
    {
    }

    public CloudflareTunnelRoute(
        string hostname,
        string service,
        CloudflareOriginRequestSettings? originRequest)
    {
        Hostname = hostname;
        Service = service;
        OriginRequest = originRequest ?? CloudflareOriginRequestSettings.Default;
    }

    public string Hostname { get; init; }
    public string Service { get; init; }
    public CloudflareOriginRequestSettings OriginRequest { get; init; }
    public bool NoTlsVerify => OriginRequest.NoTlsVerify;
}
