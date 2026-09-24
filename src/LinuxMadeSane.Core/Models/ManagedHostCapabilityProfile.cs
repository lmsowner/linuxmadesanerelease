// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Core.Models;

public sealed record ManagedHostCapabilityProfile(
    ManagedHostKind Kind,
    string KindLabel,
    string KindDescription,
    bool IsLmsHost,
    bool IsLocalLmsHost,
    bool SupportsTerminal,
    bool SupportsFileBrowser,
    bool SupportsCloudflarePublishing,
    bool SupportsCaddyIntegration,
    bool SupportsLocalLmsModules,
    bool SupportsHostDetailsIntegrations,
    bool SupportsLmsInstall,
    string LmsInstallSupportMessage);

public static class ManagedHostCapabilities
{
    public static ManagedHostCapabilityProfile Describe(ManagedHost host)
    {
        var isLocalLmsHost = AiLocalMachine.IsLocalMachine(host.Id);
        var isLmsHost = isLocalLmsHost || host.Kind == ManagedHostKind.LmsHost;
        var supportsCaddyIntegration = isLocalLmsHost;
        var supportsLocalLmsModules = isLocalLmsHost;
        var lmsInstallSupport = ResolveLmsInstallSupport(host, isLmsHost, isLocalLmsHost);
        var kindLabel = isLocalLmsHost
            ? "Local LMS host"
            : isLmsHost
                ? "LMS host"
                : "SSH host";
        var kindDescription = isLocalLmsHost
            ? "This is the machine running Linux Made Sane, so local-only LMS modules apply directly here."
            : isLmsHost
                ? "This machine is registered as another Linux Made Sane host, but local-only LMS modules still stay on the box that is currently running this UI."
                : "This machine is managed over SSH only.";

        return new ManagedHostCapabilityProfile(
            host.Kind,
            kindLabel,
            kindDescription,
            isLmsHost,
            isLocalLmsHost,
            SupportsTerminal(host),
            SupportsFileBrowser(host),
            SupportsCloudflarePublishing(host),
            supportsCaddyIntegration,
            supportsLocalLmsModules,
            supportsCaddyIntegration || SupportsCloudflarePublishing(host),
            lmsInstallSupport.SupportsInstall,
            lmsInstallSupport.Message);
    }

    public static bool IsLmsHost(ManagedHost host) => Describe(host).IsLmsHost;

    public static bool SupportsLmsInstall(ManagedHost host) => Describe(host).SupportsLmsInstall;

    public static bool SupportsTerminal(ManagedHost host) => true;

    public static bool SupportsFileBrowser(ManagedHost host) => true;

    public static bool SupportsCloudflarePublishing(ManagedHost host) => true;

    public static bool SupportsCaddyIntegration(ManagedHost host) => Describe(host).SupportsCaddyIntegration;

    public static int GetSortRank(ManagedHost host) => Describe(host) switch
    {
        { IsLocalLmsHost: true } => 0,
        { IsLmsHost: true } => 1,
        _ => 2
    };

    private static LmsInstallSupport ResolveLmsInstallSupport(ManagedHost host, bool isLmsHost, bool isLocalLmsHost)
    {
        if (isLocalLmsHost)
        {
            return new LmsInstallSupport(false, "This machine is already the local Linux Made Sane host.");
        }

        if (isLmsHost)
        {
            return new LmsInstallSupport(false, "This host is already registered as an LMS host. Use Update LMS instead.");
        }

        var hostSignals = BuildHostSignalText(host);
        if (ContainsAnySignal(hostSignals, "truenas", "freenas"))
        {
            return new LmsInstallSupport(false, "TrueNAS is treated as a storage appliance. Use it as an SSH/file host, not an LMS install target.");
        }

        if (ContainsAnySignal(hostSignals, "proxmox") || ContainsSignalPrefix(hostSignals, "pve"))
        {
            return new LmsInstallSupport(false, "Proxmox is treated as a hypervisor appliance. Keep it as an SSH host and install LMS inside a normal Linux VM or server instead.");
        }

        if (ContainsAnySignal(hostSignals, "pfsense", "opnsense", "openwrt", "routeros"))
        {
            return new LmsInstallSupport(false, "Firewall and router appliances are not supported LMS install targets. Use this machine as an SSH host only.");
        }

        if (ContainsAnySignal(hostSignals, "synology", "qnap", "unraid", "esxi", "vmware", "xcp ng"))
        {
            return new LmsInstallSupport(false, "NAS and hypervisor appliances are not supported LMS install targets. Use this machine as an SSH/file host only.");
        }

        var nonLinuxPlatform = GetNonLinuxPlatformName(hostSignals);
        if (!string.IsNullOrWhiteSpace(nonLinuxPlatform))
        {
            return new LmsInstallSupport(false, $"LMS installs are supported on Linux hosts. This host looks like {nonLinuxPlatform}, so it can be used as an SSH host only.");
        }

        return new LmsInstallSupport(true, "This host can be converted to an LMS host.");
    }

    private static string? GetNonLinuxPlatformName(string hostSignals)
    {
        if (ContainsAnySignal(hostSignals, "windows", "win32", "microsoft windows"))
        {
            return "Windows";
        }

        if (ContainsAnySignal(hostSignals, "darwin", "macos", "mac os", "os x"))
        {
            return "macOS";
        }

        if (ContainsAnySignal(hostSignals, "freebsd", "openbsd", "netbsd", "dragonflybsd", "bsd"))
        {
            return "BSD";
        }

        return null;
    }

    private static string BuildHostSignalText(ManagedHost host)
    {
        string?[] signals =
        [
            host.Name,
            host.Hostname,
            host.Environment,
            host.Description,
            host.DefaultWorkingDirectory,
            host.Platform
        ];

        return NormalizeSignalText(string.Join(' ', signals.Where(signal => !string.IsNullOrWhiteSpace(signal))));
    }

    private static bool ContainsAnySignal(string hostSignals, params string[] tokens) =>
        tokens.Any(token => hostSignals.Contains(NormalizeSignalText(token), StringComparison.Ordinal));

    private static bool ContainsSignalPrefix(string hostSignals, string prefix) =>
        hostSignals.Contains($" {NormalizeSignalToken(prefix)}", StringComparison.Ordinal);

    private static string NormalizeSignalToken(string value) =>
        NormalizeSignalText(value).Trim();

    private static string NormalizeSignalText(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        var previousWasSpace = true;
        builder.Append(' ');

        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSpace = false;
                continue;
            }

            if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        if (!previousWasSpace)
        {
            builder.Append(' ');
        }

        return builder.ToString();
    }

    private sealed record LmsInstallSupport(bool SupportsInstall, string Message);
}
