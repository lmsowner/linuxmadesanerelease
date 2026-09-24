// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.HomeLab;
using LinuxMadeSane.Core.Models.HomeLab;

namespace LinuxMadeSane.Infrastructure.Services;

internal static partial class HomeLabContainerPortPlan
{
    public static int Resolve(HomeLabPortManifest port, bool useVpnNamespacePort) =>
        useVpnNamespacePort && port.VpnContainerPort is int vpnPort
            ? vpnPort
            : port.ContainerPort;

    public static IReadOnlyList<(string Protocol, int Port)> GatewayPublishedPorts()
    {
        var assignments = HomeLabCatalog.Apps
            .Where(app => app.SupportsVpnGateway)
            .SelectMany(app => app.Ports.Select(port => new VpnListenerAssignment(
                app.Id,
                app.Name,
                port.Name,
                port.Protocol.ToLowerInvariant(),
                Resolve(port, true))))
            .ToArray();
        var collision = assignments
            .GroupBy(assignment => assignment.Port)
            .FirstOrDefault(group => group.Count() > 1);
        if (collision is not null)
        {
            var owners = string.Join(", ", collision.Select(assignment => $"{assignment.AppName} ({assignment.AppId}:{assignment.PortName}/{assignment.Protocol})"));
            throw new InvalidOperationException(
                $"Home Lab VPN listener collision: {owners} all require port {collision.Key}. Every VPN-capable app must have a globally unique namespace port.");
        }

        return assignments
            .Select(assignment => (assignment.Protocol, assignment.Port))
            .OrderBy(port => port.Item2)
            .ThenBy(port => port.Item1, StringComparer.Ordinal)
            .ToArray();
    }

    public static string GatewayFirewallInputPorts() =>
        string.Join(",", GatewayPublishedPorts()
            .Select(port => port.Port)
            .Distinct()
            .Order());

    public static string? BuildVpnFileOverrideCommand(HomeLabAppManifest app, bool usesVpnNamespace)
    {
        if (!usesVpnNamespace)
        {
            return null;
        }

        var overrides = app.Ports
            .Where(port => port.VpnContainerPort is not null && port.VpnFileOverride is not null)
            .Select(port => (Port: Resolve(port, true), Override: port.VpnFileOverride!))
            .ToArray();
        if (overrides.Length == 0)
        {
            return null;
        }

        var entrypoints = overrides
            .Select(item => item.Override.OriginalEntrypoint)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (entrypoints.Length != 1)
        {
            throw new InvalidOperationException($"{app.Name} has incompatible VPN listener entrypoints in its manifest.");
        }

        var commands = new List<string>();
        foreach (var item in overrides)
        {
            if (!SafeSettingName().IsMatch(item.Override.Setting))
            {
                throw new InvalidOperationException($"{app.Name} has an invalid VPN listener setting name.");
            }

            var path = ShellQuote(item.Override.Path);
            var setting = item.Override.Setting;
            commands.Add(
                $"grep -q '^{setting}=' {path} || {{ echo 'Missing {setting} in {item.Override.Path}' >&2; exit 1; }}");
            commands.Add($"sed -i 's|^{setting}=.*$|{setting}={item.Port}|' {path}");
        }

        commands.Add($"exec {ShellQuote(entrypoints[0])}");
        return string.Join("; ", commands);
    }

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    [GeneratedRegex("^[A-Z][A-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSettingName();

    private sealed record VpnListenerAssignment(
        string AppId,
        string AppName,
        string PortName,
        string Protocol,
        int Port);
}
