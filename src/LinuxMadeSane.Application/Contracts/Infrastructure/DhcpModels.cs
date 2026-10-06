// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.Infrastructure;

public sealed class DhcpEditor
{
    public bool Enabled { get; set; }
    public bool AdoptExisting { get; set; }
    public bool ConfirmSoleDhcpServer { get; set; }
    public string OriginalHash { get; set; } = "";
    public uint SubnetId { get; set; } = 1;
    public string Interface { get; set; } = "";
    public string Subnet { get; set; } = "";
    public string PoolStart { get; set; } = "";
    public string PoolEnd { get; set; } = "";
    public string Gateway { get; set; } = "";
    public string DnsServers { get; set; } = "";
    public int LeaseSeconds { get; set; } = 86400;
    public List<DhcpReservation> Reservations { get; set; } = [];
}
public sealed record DhcpReservation(string Mac, string Address, string Hostname);
public sealed record DhcpLease(string Address, string Mac, string Hostname, DateTimeOffset ExpiresUtc, bool Active);
public sealed record DhcpWorkspace(DhcpEditor Editor, IReadOnlyList<string> Interfaces, IReadOnlyList<DhcpLease> Leases,
    IReadOnlyList<string> Notices, bool ReadOnly, string ExistingConfiguration)
{
    public bool DocumentationConfiguration { get; init; }
    public IReadOnlyList<DhcpInterfaceNetwork> InterfaceNetworks { get; init; } = [];
}
public sealed record DhcpInterfaceNetwork(string Interface, string Address, string Network);
