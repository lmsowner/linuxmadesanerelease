// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using LinuxMadeSane.Core.Enums;
namespace LinuxMadeSane.Application.Contracts.Security;

public enum SshForwardMode { Local, Remote, LocalSocks, RemoteSocks }
public enum SshForwardEndpointKind { Tcp, UnixSocket }
public sealed record SshPortForward
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Guid CredentialId { get; set; }
    public string Server { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string Username { get; set; } = "";
    public SshForwardMode Mode { get; set; }
    public SshForwardEndpointKind ListenKind { get; set; }
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; } = 8080;
    public string ListenSocket { get; set; } = "";
    public SshForwardEndpointKind TargetKind { get; set; }
    public string TargetHost { get; set; } = "127.0.0.1";
    public int TargetPort { get; set; } = 80;
    public string TargetSocket { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int KeepAliveSeconds { get; set; } = 15;
    public int MissedKeepAlives { get; set; } = 3;
    public int RetrySeconds { get; set; } = 10;
    public bool Compression { get; set; }
    public string AddressFamily { get; set; } = "auto";
    public string OutboundBindAddress { get; set; } = "";
    public bool ReplaceStaleSocket { get; set; }
    public string SocketMask { get; set; } = "0177";
    public Guid? EdgeGatewayRouteId { get; set; }
    public string PublicUrl { get; set; } = "";
    public bool IsSocks => Mode is SshForwardMode.LocalSocks or SshForwardMode.RemoteSocks;
    public bool IsRemote => Mode is SshForwardMode.Remote or SshForwardMode.RemoteSocks;
    public bool CanLinkCaddy => Mode == SshForwardMode.Local && ListenKind == SshForwardEndpointKind.Tcp &&
        (ListenAddress == "localhost" || System.Net.IPAddress.TryParse(ListenAddress, out var address) && System.Net.IPAddress.IsLoopback(address));
}
public sealed record SshForwardStatus(string State, string Detail, DateTimeOffset? ConnectedAtUtc = null,
    DateTimeOffset? LastFailureAtUtc = null, int Restarts = 0, int? ProcessId = null, int? AllocatedListenPort = null, string? LastFailureDetail = null);
public sealed record SshForwardDiagnostic(DateTimeOffset AtUtc, string State, string Detail);
public sealed record SshForwardView(SshPortForward Forward, SshForwardStatus Status)
{
    public IReadOnlyList<SshForwardDiagnostic> Diagnostics { get; init; } = [];
}
public sealed record SshForwardCaddyLink(string Hostname, string DomainName,
    EdgeGatewayTargetScheme Scheme = EdgeGatewayTargetScheme.Http, EdgeGatewayAuthMode AuthMode = EdgeGatewayAuthMode.RequireMfa);
