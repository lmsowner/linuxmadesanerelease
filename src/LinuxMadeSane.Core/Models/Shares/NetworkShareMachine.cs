namespace LinuxMadeSane.Core.Models.Shares;

public sealed record NetworkShareMachine(
    Guid Id,
    string DisplayName,
    string Target,
    string? IpAddress,
    string? Workgroup,
    string DiscoveryMethod,
    string? OperatingSystem,
    string? ServerVersion);
