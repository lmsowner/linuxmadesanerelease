// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;

namespace LinuxMadeSane.Application.Contracts.Infrastructure;

public sealed class NetworkDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Hostname { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public List<string> Addresses { get; set; } = [];
    public string Mac { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Interface { get; set; } = "";
    public string Subnet { get; set; } = "";
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public string State { get; set; } = "Not recently observed";
    public List<string> Sources { get; set; } = [];
    public List<string> Services { get; set; } = [];
    public Dictionary<string, string> ServiceUrls { get; set; } = [];
    public string DhcpState { get; set; } = "Unknown";
    public Guid? LmsHostId { get; set; }
    public DateTimeOffset? LastDnsLookupUtc { get; set; }
    public string NameLookupStatus { get; set; } = "No lookup recorded";
    public string Notes { get; set; } = "";
    public string Tags { get; set; } = "";
}

public sealed record InterfaceDnsConfiguration(string Interface, IReadOnlyList<string> Servers, string Source)
{
    public IReadOnlyList<string> DhcpServers { get; init; } = [];
    public string DhcpServer { get; init; } = "";
}

public sealed record DeviceInventory(IReadOnlyList<NetworkDevice> Devices, IReadOnlyList<string> Notices)
{
    public IReadOnlyList<DhcpInterfaceNetwork> InterfaceNetworks { get; init; } = [];
    public IReadOnlyList<InterfaceDnsConfiguration> InterfaceDns { get; init; } = [];
}
public sealed record ExposureEntry(string Service, string Local, string External, string Method, IReadOnlyList<string> Reasoning);
public sealed record ExposureSnapshot(IReadOnlyList<ExposureEntry> Entries, IReadOnlyList<string> Notices);
public sealed record TimeDiagnostics(DateTimeOffset LocalTime, DateTimeOffset UtcTime, string Timezone,
    string Provider, string Service, string ServiceState, string Synchronization, string SourceDetails,
    IReadOnlyList<string> Notices)
{
    public string ActiveSource { get; init; } = "Not reported";
    public string EstimatedOffset { get; init; } = "Not reported";
    public string LastSynchronization { get; init; } = "Not reported";
}
public sealed record SmartHealth(string Device, bool Available, bool? Healthy, string Model,
    int? Temperature, long? PowerOnHours, int? WearPercent, IReadOnlyDictionary<string, string> Details,
    IReadOnlyList<string> Warnings, string RawJson, string Explanation);
public sealed record FeaturePackageStatus(string Feature, IReadOnlyList<string> Packages, bool Installed,
    bool CanInstall, string Explanation);
