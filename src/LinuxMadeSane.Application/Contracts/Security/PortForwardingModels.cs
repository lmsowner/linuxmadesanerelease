// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.Security;

public enum PortForwardProtocol
{
    Tcp = 0,
    Udp = 1
}

public sealed class PortForwardEditor
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int SourcePort { get; set; }
    public PortForwardProtocol Protocol { get; set; } = PortForwardProtocol.Tcp;
    public string DestinationAddress { get; set; } = string.Empty;
    public int DestinationPort { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public sealed record PortForwardInterfaceOption(
    string Address,
    string InterfaceName,
    string Label,
    bool IsWildcard,
    bool IsIpv6);

public sealed record PortForwardRuleViewModel(
    Guid Id,
    string Name,
    string ListenAddress,
    string InterfaceName,
    int SourcePort,
    PortForwardProtocol Protocol,
    string DestinationAddress,
    int DestinationPort,
    bool IsEnabled,
    bool IsRunning,
    string Status,
    string UnitName);

public sealed record PortForwardPortCheck(
    bool IsAvailable,
    string Summary,
    string? ListenerProcess = null);

public sealed record PortForwardingOverview(
    bool IsSocatInstalled,
    bool CanManage,
    string SocatVersion,
    IReadOnlyList<PortForwardInterfaceOption> Interfaces,
    IReadOnlyList<PortForwardRuleViewModel> Rules,
    string? Warning = null);
