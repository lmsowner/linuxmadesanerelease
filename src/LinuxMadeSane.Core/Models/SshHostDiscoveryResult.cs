namespace LinuxMadeSane.Core.Models;

public sealed record SshHostDiscoveryResult(
    IReadOnlyList<DiscoveredSshHost> Hosts,
    string StatusMessage,
    IReadOnlyList<string> Notes);
