// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Net;
using System.Runtime.InteropServices;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Models.Ai;

public static class AiLocalMachine
{
    public static readonly Guid ManagedHostId = Guid.Parse("2f9c87a6-5e8d-4b0a-b5fc-0a5b1fe9d13f");
    public const int DefaultPort = 22;

    public const string Name = "Local machine";
    public const string Hostname = "localhost";
    public const string EnvironmentLabel = "Local";
    public const string Description = "The machine running Linux Made Sane. Commands and file access execute locally, not over SSH.";

    public static ManagedHost CreateManagedHost()
    {
        var defaultWorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(defaultWorkingDirectory))
        {
            defaultWorkingDirectory = "/";
        }

        var platform = RuntimeInformation.OSDescription.Trim();
        if (string.IsNullOrWhiteSpace(platform))
        {
            platform = "Linux";
        }

        return new ManagedHost(
            ManagedHostId,
            Name,
            Hostname,
            DefaultPort,
            EnvironmentLabel,
            Description,
            defaultWorkingDirectory,
            HostOperatingStatus.Online,
            AuthenticationType.Password,
            null,
            Environment.UserName,
            null,
            null,
            null,
            false,
            DateTimeOffset.UtcNow,
            ConnectionTestStatus.Succeeded,
            platform,
            ManagedHostKind.LmsHost);
    }

    public static AiAttachedServer CreateAttachedServer(Guid threadId)
    {
        var host = CreateManagedHost();

        return new AiAttachedServer(
            Guid.NewGuid(),
            threadId,
            host.Id,
            host.Name,
            host.Hostname,
            host.Environment,
            DateTimeOffset.UtcNow);
    }

    public static IReadOnlyList<AiAttachedServer> GetEffectiveAttachedServers(
        Guid threadId,
        IReadOnlyList<AiAttachedServer> attachedServers) =>
        attachedServers.Count > 0
            ? attachedServers
            : [CreateAttachedServer(threadId)];

    public static bool IsLocalMachine(Guid managedHostId) =>
        managedHostId == ManagedHostId;

    public static bool IsLoopbackHostname(string? hostname)
    {
        var normalizedHostname = NormalizeHostname(hostname);
        if (string.IsNullOrWhiteSpace(normalizedHostname))
        {
            return false;
        }

        if (normalizedHostname.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(normalizedHostname, out var address) &&
               IPAddress.IsLoopback(address);
    }

    private static string NormalizeHostname(string? hostname)
    {
        var trimmed = hostname?.Trim() ?? string.Empty;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            trimmed = uri.Host;
        }
        else if (trimmed.StartsWith('[') && trimmed.IndexOf(']') is var bracketEnd and > 0)
        {
            trimmed = trimmed[1..bracketEnd];
        }
        else if (trimmed.Count(static character => character == ':') == 1 &&
                 trimmed.LastIndexOf(':') is var portSeparatorIndex and > 0 &&
                 int.TryParse(trimmed[(portSeparatorIndex + 1)..], out _))
        {
            trimmed = trimmed[..portSeparatorIndex];
        }

        return trimmed.Trim('[', ']');
    }
}
