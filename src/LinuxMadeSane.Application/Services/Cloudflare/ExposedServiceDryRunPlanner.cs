// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Application.Services.Cloudflare;

public static class ExposedServiceDryRunPlanner
{
    public static ExposedServiceDryRunPlan Build(ExposedServicePlanningContext context)
    {
        var steps = new List<ExposedServicePlanStep>
        {
            new(
                context.HasExistingTunnel ? "Reuse tunnel" : "Create tunnel",
                context.HasExistingTunnel
                    ? $"Reuse managed Cloudflare Tunnel {context.TunnelName}."
                    : $"Create managed Cloudflare Tunnel {context.TunnelName}.",
                !context.HasExistingTunnel)
        };

        steps.Add(context.DnsConflictKind switch
        {
            CloudflareDnsConflictKind.None => new ExposedServicePlanStep(
                "Create DNS",
                $"Create proxied CNAME {context.Hostname} -> {context.DnsTargetDescription}.",
                true),
            CloudflareDnsConflictKind.Reuse => new ExposedServicePlanStep(
                "Reuse DNS",
                $"Keep existing CNAME for {context.Hostname} because it already points at the tunnel.",
                false),
            CloudflareDnsConflictKind.Update => new ExposedServicePlanStep(
                "Update DNS",
                $"Update existing managed DNS record for {context.Hostname} to point at {context.DnsTargetDescription}.",
                true),
            _ => new ExposedServicePlanStep(
                "Replace DNS",
                $"Delete the existing DNS record for {context.Hostname}, then create proxied CNAME {context.Hostname} -> {context.DnsTargetDescription}.",
                true)
        });

        steps.Add(new ExposedServicePlanStep(
            context.HasHostnameRoute ? "Update hostname route" : "Create hostname route",
            $"{(context.HasHostnameRoute ? "Update" : "Create")} the tunnel route {context.Hostname} -> {context.LocalServiceUrl}{(context.NoTlsVerify ? " with origin TLS verification disabled." : ".")}",
            true));

        steps.Add(new ExposedServicePlanStep(
            "Connect target host",
            context.ConnectorMatchesSelectedTunnel
                ? "Reuse the existing cloudflared service already installed on this managed host for the selected tunnel. No reinstall is needed."
                : context.HasInstalledConnector
                    ? "This host already has cloudflared installed for a different tunnel. Reuse that installed tunnel instead of trying to create or attach another local cloudflared service."
            : context.RunConnectorInstallOnHost
                ? "Linux Made Sane will install or attach cloudflared on this managed host after apply. If automatic setup fails, the command will still be shown for manual recovery."
                : "Run the Cloudflare-provided cloudflared connector command on the machine serving the local service URL after apply. Existing tunnels can front multiple hostnames, so reuse one unless you need a separate connector identity.",
            false));

        if (!context.AccessEnabled)
        {
            steps.Add(new ExposedServicePlanStep(
                "Skip Access",
                "Skip Cloudflare Access application and policy creation.",
                false));
        }
        else
        {
            steps.Add(new ExposedServicePlanStep(
                context.HasAccessApplication ? "Update Access app" : "Create Access app",
                $"{(context.HasAccessApplication ? "Update" : "Create")} self-hosted Access app for {context.Hostname}.",
                true));
            steps.Add(new ExposedServicePlanStep(
                context.HasAccessPolicy ? "Update Access policy" : "Create Access policy",
                $"{(context.HasAccessPolicy ? "Update" : "Create")} default allow policy for the chosen identity scope.",
                true));
        }

        return new ExposedServiceDryRunPlan(
            context.Hostname,
            context.PublicUrl,
            context.TunnelName,
            context.AccessEnabled,
            context.Warnings.Any(item => item.RequiresConfirmation),
            context.Warnings,
            steps);
    }
}

public sealed record ExposedServicePlanningContext(
    string Hostname,
    string PublicUrl,
    string TunnelName,
    string LocalServiceUrl,
    string DnsTargetDescription,
    bool AccessEnabled,
    CloudflareDnsConflictKind DnsConflictKind,
    string DnsConflictReason,
    bool HasExistingTunnel,
    bool HasHostnameRoute,
    bool HasAccessApplication,
    bool HasAccessPolicy,
    bool HasInstalledConnector,
    bool ConnectorMatchesSelectedTunnel,
    bool RunConnectorInstallOnHost,
    IReadOnlyList<ExposureWarning> Warnings,
    bool NoTlsVerify = false);
