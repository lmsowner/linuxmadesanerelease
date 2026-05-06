namespace LinuxMadeSane.Core.Models;

public sealed record DiscoveredSshHost(
    string DisplayName,
    string Target,
    string? IpAddress,
    int Port,
    string ScopeLabel,
    string SourceLabel,
    string? Platform,
    string? SshBanner);
