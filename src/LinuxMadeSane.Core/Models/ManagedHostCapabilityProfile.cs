// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

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
    bool SupportsHostDetailsIntegrations);

public static class ManagedHostCapabilities
{
    public static ManagedHostCapabilityProfile Describe(ManagedHost host)
    {
        var isLocalLmsHost = AiLocalMachine.IsLocalMachine(host.Id);
        var isLmsHost = isLocalLmsHost || host.Kind == ManagedHostKind.LmsHost;
        var supportsCaddyIntegration = isLocalLmsHost;
        var supportsLocalLmsModules = isLocalLmsHost;
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
            supportsCaddyIntegration || SupportsCloudflarePublishing(host));
    }

    public static bool IsLmsHost(ManagedHost host) => Describe(host).IsLmsHost;

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
}
