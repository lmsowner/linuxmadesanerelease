namespace LinuxMadeSane.Infrastructure.Services;

public sealed record HttpServiceDiscoveryStorageSettings(string RootDirectory)
{
    public string CachePath => Path.Combine(RootDirectory, "http-services-cache.json");
}
