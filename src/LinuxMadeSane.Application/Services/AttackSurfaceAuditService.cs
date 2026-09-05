// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text;
using LinuxMadeSane.Application.Contracts.Security;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.EdgeGateway;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Services;

public sealed class AttackSurfaceAuditService(
    IFirewallManagementService firewallManagementService,
    ISecuritySettingsService securitySettingsService,
    ISecurityPasskeyStore passkeyStore,
    IEdgeGatewayStore edgeGatewayStore,
    IAiProviderRegistry providerRegistry,
    ILinuxCommandRunner commandRunner) : IAttackSurfaceAuditService
{
    public async Task<AttackSurfaceAiAvailability> GetAiAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        var configuredProviders = await providerRegistry.ListConfiguredProvidersAsync(cancellationToken);
        var candidates = configuredProviders
            .Where(provider =>
                provider.IsEnabled &&
                providerRegistry.FindDefinition(provider.ProviderType)?.IsRuntimeImplemented != false)
            .OrderByDescending(provider => provider.IsDefault)
            .ThenBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var candidate in candidates)
        {
            var models = await providerRegistry.ListModelsAsync(candidate.ProviderKey, cancellationToken);
            var modelId = !string.IsNullOrWhiteSpace(candidate.DefaultModelId)
                ? candidate.DefaultModelId.Trim()
                : models.FirstOrDefault()?.ModelId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(modelId))
            {
                continue;
            }

            return new AttackSurfaceAiAvailability(
                true,
                candidate.ProviderKey,
                string.IsNullOrWhiteSpace(candidate.DisplayName)
                    ? providerRegistry.FindDefinition(candidate.ProviderType)?.DisplayName ?? candidate.ProviderType.ToString()
                    : candidate.DisplayName,
                modelId);
        }

        return new AttackSurfaceAiAvailability(false, string.Empty, string.Empty, string.Empty);
    }

    public async Task<AttackSurfaceAuditResult> RunAsync(
        bool includeAiAnalysis,
        CancellationToken cancellationToken = default)
    {
        // These stores can share one scoped EF Core context, so keep their reads sequential.
        var firewall = await firewallManagementService.GetStatusAsync(cancellationToken);
        var security = await securitySettingsService.GetPageAsync(cancellationToken);
        var routes = await edgeGatewayStore.ListRoutesAsync(cancellationToken);
        var aiAvailability = await GetAiAvailabilityAsync(cancellationToken);
        var passkeyCounts = new Dictionary<Guid, int>();
        foreach (var user in security.Users.Where(user => user.IsEnabled))
        {
            passkeyCounts[user.Id] = (await passkeyStore.ListByUserAsync(user.Id, cancellationToken)).Count;
        }

        var cloudflareTunnelActive = await InspectCloudflareTunnelAsync(cancellationToken);
        var staticAssessment = AttackSurfaceAuditBuilder.Build(
            firewall,
            security,
            routes,
            passkeyCounts,
            cloudflareTunnelActive);
        var findings = staticAssessment.Findings;
        string? aiAnalysis = null;
        string? aiAnalysisError = null;

        if (includeAiAnalysis)
        {
            if (!aiAvailability.IsAvailable)
            {
                aiAnalysisError = "No enabled runnable AI provider with a model is configured.";
            }
            else
            {
                try
                {
                    aiAnalysis = await AnalyzeWithAiAsync(
                        aiAvailability,
                        staticAssessment.Posture,
                        firewall.ListeningPorts,
                        findings,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    aiAnalysisError = $"The static audit completed, but AI analysis failed: {exception.Message}";
                }
            }
        }

        return new AttackSurfaceAuditResult(
            DateTimeOffset.UtcNow,
            staticAssessment.Posture,
            findings,
            firewall.ListeningPorts,
            aiAvailability,
            aiAnalysis,
            aiAnalysisError);
    }

    private async Task<string> AnalyzeWithAiAsync(
        AttackSurfaceAiAvailability availability,
        AttackSurfacePosture posture,
        IReadOnlyList<FirewallListeningPortViewModel> listeners,
        IReadOnlyList<AttackSurfaceFinding> findings,
        CancellationToken cancellationToken)
    {
        var provider = await providerRegistry.GetProviderAsync(availability.ProviderKey, cancellationToken)
            ?? throw new InvalidOperationException("The selected AI provider is no longer available.");
        var now = DateTimeOffset.UtcNow;
        var thread = new AiChatThread(
            Guid.NewGuid(),
            "Attack surface audit",
            provider.ProviderKey,
            provider.Definition.ProviderType,
            availability.ModelId,
            AiTrustProfile.CreatePreset(AiTrustLevel.Observe),
            string.Empty,
            string.Empty,
            now,
            now);
        AiProviderInputItem[] inputItems =
        [
            new AiProviderMessageInputItem(
                AiChatMessageRole.User,
                BuildAiPrompt(posture, findings, listeners))
        ];

        var result = await provider.ExecuteTurnAsync(
            new AiProviderTurnRequest(
                thread,
                [],
                [],
                inputItems,
                [],
                false,
                false),
            cancellationToken: cancellationToken);
        var analysis = string.Join(
            Environment.NewLine + Environment.NewLine,
            result.AssistantOutputs
                .Select(output => output.Content?.Trim())
                .Where(static content => !string.IsNullOrWhiteSpace(content)));

        return string.IsNullOrWhiteSpace(analysis)
            ? throw new InvalidOperationException("The AI provider returned no analysis text.")
            : analysis;
    }

    private static string BuildAiPrompt(
        AttackSurfacePosture posture,
        IReadOnlyList<AttackSurfaceFinding> findings,
        IReadOnlyList<FirewallListeningPortViewModel> listeners)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("Act as a calm, evidence-led defensive Linux security reviewer. Analyze residual risk after the observed controls, not theoretical danger before mitigations.");
        prompt.AppendLine("Never treat a listening socket as proof of internet exposure. Loopback sockets are excluded from this inventory. A non-loopback bind can still be blocked by the host firewall, cloud firewall, router, VPN, or outbound-only tunnel.");
        prompt.AppendLine("Treat key-only SSH on port 22 as a strongly mitigated, low residual-risk administration path. Treat LMS MFA or passkey authentication behind an active outbound Cloudflare Tunnel as strongly mitigated. Do not turn generic best practices or uninspected areas into findings.");
        prompt.AppendLine("Start with Overall observed posture. Include Immediate attention only when an Attention finding exists, Validate next only for Review findings, and Already guarded for effective controls. Do not claim that Cloudflare Access policy was verified unless the evidence says so. Do not propose offensive exploitation or commands that change the host.");
        prompt.AppendLine();
        prompt.Append("Static overall posture: ")
            .Append(posture.Label)
            .Append(". ")
            .AppendLine(posture.Summary);
        prompt.AppendLine("Confirmed controls:");
        foreach (var control in posture.ConfirmedControls)
        {
            prompt.Append("- ").AppendLine(control);
        }
        prompt.AppendLine();
        prompt.AppendLine("Fixed-rule findings:");
        foreach (var finding in findings)
        {
            prompt.Append("- [")
                .Append(finding.Level)
                .Append("] ")
                .Append(finding.Category)
                .Append(" / ")
                .Append(finding.Title)
                .Append(": ")
                .Append(finding.Summary)
                .Append(" Evidence: ")
                .AppendLine(finding.Evidence);
        }

        prompt.AppendLine();
        prompt.AppendLine("Non-loopback bind inventory (not proof of external reachability):");
        if (listeners.Count == 0)
        {
            prompt.AppendLine("- None observed, or the listener inventory was unavailable.");
        }
        else
        {
            foreach (var listener in listeners.Take(100))
            {
                prompt.Append("- ")
                    .Append(listener.Protocol)
                    .Append('/')
                    .Append(listener.Port)
                    .Append(" on ")
                    .AppendLine(listener.Destination);
            }
        }

        return prompt.ToString();
    }

    private async Task<bool?> InspectCloudflareTunnelAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            var result = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "systemctl",
                    ["is-active", "cloudflared.service"],
                    false,
                    TimeSpan.FromSeconds(10),
                    "Inspect cloudflared tunnel transport")
                {
                    IsOptionalExternalTool = true
                },
                dryRun: false,
                cancellationToken);
            if (result.ExitCode == 0 || result.StandardOutput.Trim().Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.IsNullOrWhiteSpace(result.StandardOutput) && string.IsNullOrWhiteSpace(result.StandardError)
                ? null
                : false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}

internal sealed record AttackSurfaceStaticAssessment(
    AttackSurfacePosture Posture,
    IReadOnlyList<AttackSurfaceFinding> Findings);

internal static class AttackSurfaceAuditBuilder
{
    private static readonly IReadOnlyDictionary<int, string> SensitivePorts = new Dictionary<int, string>
    {
        [21] = "FTP",
        [22] = "SSH/SFTP",
        [23] = "Telnet",
        [111] = "RPC bind",
        [139] = "NetBIOS file sharing",
        [445] = "SMB file sharing",
        [2049] = "NFS",
        [2375] = "unencrypted Docker API",
        [2376] = "Docker API",
        [3306] = "MySQL/MariaDB",
        [3389] = "RDP",
        [5432] = "PostgreSQL",
        [5900] = "VNC",
        [6379] = "Redis",
        [6443] = "Kubernetes API",
        [9200] = "Elasticsearch",
        [27017] = "MongoDB"
    };

    private static readonly IReadOnlySet<int> InherentlyUnsafePorts = new HashSet<int>
    {
        23,
        2375
    };

    public static AttackSurfaceStaticAssessment Build(
        FirewallStatusViewModel firewall,
        SecuritySettingsPageViewModel security,
        IReadOnlyList<EdgeGatewayRoute> routes,
        IReadOnlyDictionary<Guid, int> passkeyCounts,
        bool? cloudflareTunnelActive = null)
    {
        var firewallFinding = BuildFirewallFinding(firewall);
        var permittedListeners = ResolveHostFirewallPermittedListeners(firewall);
        var networkFinding = BuildNetworkReachabilityFinding(firewall, permittedListeners);
        var remoteServiceFinding = BuildRemoteServiceFinding(firewall, security, permittedListeners);
        var identityFinding = BuildIdentityFinding(security, passkeyCounts);
        var publishedRouteFinding = BuildPublishedRouteFinding(routes, identityFinding.Level == AttackSurfaceFindingLevel.Protected);
        var findings = new List<AttackSurfaceFinding>
        {
            firewallFinding,
            networkFinding,
            remoteServiceFinding,
            publishedRouteFinding,
            identityFinding,
            BuildTrustBoundaryFinding(security)
        };

        if (cloudflareTunnelActive == true)
        {
            findings.Add(BuildCloudflareTunnelFinding(identityFinding));
        }

        return new AttackSurfaceStaticAssessment(
            BuildPosture(
                findings,
                firewallFinding,
                remoteServiceFinding,
                publishedRouteFinding,
                identityFinding,
                cloudflareTunnelActive),
            findings);
    }

    private static AttackSurfaceFinding BuildFirewallFinding(FirewallStatusViewModel firewall)
    {
        if (!firewall.IsInstalled || !firewall.CanManage)
        {
            return new AttackSurfaceFinding(
                "firewall",
                "Network boundary",
                "Host firewall coverage",
                AttackSurfaceFindingLevel.Review,
                "LMS could not verify the host firewall policy. This is a visibility gap, not evidence that the host is publicly exposed.",
                firewall.Warning ?? (firewall.IsInstalled ? "UFW is installed but its effective policy was unavailable." : "UFW is not installed."),
                "Confirm whether a cloud firewall, router ACL, VPN policy, or another host firewall provides the inbound boundary.");
        }

        if (!firewall.IsActive || !firewall.IncomingPolicy.Equals("deny", StringComparison.OrdinalIgnoreCase))
        {
            return new AttackSurfaceFinding(
                "firewall",
                "Network boundary",
                "Host firewall coverage",
                AttackSurfaceFindingLevel.Review,
                firewall.IsActive
                    ? "UFW is active, but its incoming default is not deny; upstream controls may still restrict access."
                    : "UFW is installed but inactive; this does not establish what a cloud firewall, router, or tunnel permits.",
                $"Active: {firewall.IsActive}; incoming: {firewall.IncomingPolicy}; routed: {firewall.RoutedPolicy}; {firewall.Rules.Count(rule => rule.IsEnabled)} enabled rule(s).",
                "Use a safe rollback window to move toward default-deny inbound policy, then allow only required sources and ports.");
        }

        return new AttackSurfaceFinding(
            "firewall",
            "Network boundary",
            "Host firewall coverage",
            AttackSurfaceFindingLevel.Protected,
            "UFW is active with default-deny inbound policy.",
            $"Incoming: {firewall.IncomingPolicy}; routed: {firewall.RoutedPolicy}; {firewall.Rules.Count(rule => rule.IsEnabled)} enabled rule(s).",
            "Review allow rules against the listener inventory and remove rules that no longer support a known service.");
    }

    private static AttackSurfaceFinding BuildNetworkReachabilityFinding(
        FirewallStatusViewModel firewall,
        IReadOnlyList<FirewallListeningPortViewModel> permittedListeners)
    {
        var listeners = firewall.ListeningPorts;
        if (listeners.Count == 0)
        {
            var inventoryUnavailable = firewall.Warning?.Contains("listen", StringComparison.OrdinalIgnoreCase) == true ||
                                       firewall.Warning?.Contains("inventory", StringComparison.OrdinalIgnoreCase) == true;
            return new AttackSurfaceFinding(
                "listeners",
                "Network services",
                "Network bind inventory",
                inventoryUnavailable ? AttackSurfaceFindingLevel.Review : AttackSurfaceFindingLevel.Protected,
                inventoryUnavailable
                    ? "LMS could not read the non-loopback bind inventory. This is missing evidence, not a detected exposure."
                    : "No non-loopback TCP or UDP binds were observed. Loopback sockets are deliberately ignored because they are not remote entry points.",
                inventoryUnavailable
                    ? firewall.Warning!
                    : "The inventory excludes 127.0.0.0/8, ::1, and foreign addresses.",
                inventoryUnavailable
                    ? "Validate the inventory if an exact exposure map is required."
                    : "No action is suggested from listener state alone.");
        }

        if (HasDefaultDenyFirewall(firewall))
        {
            return new AttackSurfaceFinding(
                "listeners",
                "Network services",
                "Network binds filtered by firewall policy",
                AttackSurfaceFindingLevel.Protected,
                permittedListeners.Count == 0
                    ? $"{listeners.Count} non-loopback bind(s) were observed, but none maps to an enabled UFW inbound allow rule. A bind is not internet exposure."
                    : $"{listeners.Count} non-loopback bind(s) were observed; {permittedListeners.Count} maps to an explicit UFW inbound allow rule and is assessed separately.",
                $"UFW is active with deny-incoming default; locally bound ports: {FormatPorts(listeners)}; explicitly permitted: {FormatPorts(permittedListeners)}.",
                "No action is suggested for blocked or loopback-only services. Review only the deliberately permitted entry points below.");
        }

        return new AttackSurfaceFinding(
            "listeners",
            "Network services",
            "Network reachability needs boundary context",
            AttackSurfaceFindingLevel.Review,
            $"{listeners.Count} non-loopback bind(s) exist, but LMS cannot prove whether a cloud firewall, router, VPN, or proxy makes them externally reachable.",
            $"Observed local bind ports: {FormatPorts(listeners)}. Loopback sockets are excluded.",
            "Validate the upstream boundary only if these services are intended to be private; do not infer exposure from the bind alone.");
    }

    private static AttackSurfaceFinding BuildRemoteServiceFinding(
        FirewallStatusViewModel firewall,
        SecuritySettingsPageViewModel security,
        IReadOnlyList<FirewallListeningPortViewModel> permittedListeners)
    {
        var candidates = HasDefaultDenyFirewall(firewall)
            ? permittedListeners
            : firewall.ListeningPorts;
        var sensitiveListeners = candidates
            .Where(listener => SensitivePorts.ContainsKey(listener.Port))
            .ToArray();
        var sensitive = sensitiveListeners
            .Select(listener => $"{SensitivePorts[listener.Port]} ({listener.Protocol}/{listener.Port})")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sensitive.Length == 0)
        {
            return new AttackSurfaceFinding(
                "remote-services",
                "Remote administration and data",
                "Permitted high-value services",
                AttackSurfaceFindingLevel.Protected,
                HasDefaultDenyFirewall(firewall)
                    ? "No commonly sensitive administration, file-sharing, database, or orchestration port is permitted by the observed host firewall rules."
                    : "No commonly sensitive service port was identified in the non-loopback bind sample.",
                "The port catalogue is limited and does not attempt to guess service identity on custom ports.",
                "No action is suggested from this check.");
        }

        var sshUsers = security.Users
            .Where(user => user.IsEnabled && !string.IsNullOrWhiteSpace(user.LinuxUsername))
            .ToArray();
        var keyOnlySsh = sshUsers.Length > 0 && sshUsers.All(user =>
            user.SshAuthenticationMode == RemoteAccessSshAuthenticationMode.KeyOnly &&
            user.HasAuthorizedKeys);
        var onlySsh = sensitiveListeners.All(listener => listener.Port == 22);
        if (onlySsh && keyOnlySsh)
        {
            return new AttackSurfaceFinding(
                "remote-services",
                "Remote administration and data",
                "Key-only SSH administration",
                AttackSurfaceFindingLevel.Protected,
                "The only observed permitted high-value service is SSH on port 22, and every enabled LMS-managed SSH account is key-only with authorized keys. This is a low residual-risk administration path.",
                $"SSH listeners: {string.Join(", ", sensitive)}; key-only LMS-managed accounts: {sshUsers.Length}/{sshUsers.Length}.",
                "Keep private keys protected and remove obsolete authorized keys. Unmanaged operating-system accounts are outside this check.");
        }

        if (sensitiveListeners.Any(listener => InherentlyUnsafePorts.Contains(listener.Port)))
        {
            return new AttackSurfaceFinding(
                "remote-services",
                "Remote administration and data",
                "Unencrypted remote service permitted",
                AttackSurfaceFindingLevel.Attention,
                "An explicitly permitted service uses a port associated with a clear-text protocol or unauthenticated Docker API.",
                string.Join(", ", sensitive),
                "Disable the service or restrict it to a trusted private network after confirming its owner.");
        }

        return new AttackSurfaceFinding(
            "remote-services",
            "Remote administration and data",
            "Permitted high-value service ports",
            AttackSurfaceFindingLevel.Review,
            "One or more high-value service ports are deliberately permitted, but their authentication and intended audience cannot be fully established from the listener alone.",
            string.Join(", ", sensitive),
            "Confirm the permitted service and authentication method. A listed port is not, by itself, evidence of a vulnerability.");
    }

    private static AttackSurfaceFinding BuildPublishedRouteFinding(
        IReadOnlyList<EdgeGatewayRoute> routes,
        bool lmsIdentityIsStrong)
    {
        var enabled = routes.Where(route => route.Enabled).ToArray();
        var passThrough = enabled.Count(route =>
            route.AuthMode == EdgeGatewayAuthMode.PassThrough &&
            !route.AllowLanOnly);
        var skippedTls = enabled.Count(route => route.SkipUpstreamTlsVerification);
        var stronglyAuthenticated = enabled.Count(route =>
            route.AuthMode is EdgeGatewayAuthMode.RequireMfa or EdgeGatewayAuthMode.RequirePasskey or EdgeGatewayAuthMode.Blocked ||
            (route.AuthMode == EdgeGatewayAuthMode.RequireLogin && lmsIdentityIsStrong) ||
            route.AllowLanOnly);
        var level = enabled.Length == 0 ||
                    (stronglyAuthenticated == enabled.Length && skippedTls == 0)
            ? AttackSurfaceFindingLevel.Protected
            : AttackSurfaceFindingLevel.Review;

        return new AttackSurfaceFinding(
            "published-routes",
            "Published applications",
            "Edge Gateway routes",
            level,
            enabled.Length == 0
                ? "No enabled Edge Gateway routes are configured."
                : level == AttackSurfaceFindingLevel.Protected
                    ? $"All {enabled.Length} enabled application route(s) are guarded by LMS login with strong account authentication, explicit MFA/passkey enforcement, blocking, or LAN-only scope."
                    : $"{enabled.Length} application route(s) are enabled; {stronglyAuthenticated} have a locally verifiable strong authentication or network control.",
            $"Enabled: {enabled.Length}; strongly guarded: {stronglyAuthenticated}; externally scoped pass-through authentication: {passThrough}; upstream TLS verification skipped: {skippedTls}.",
            enabled.Length == 0
                ? "No action is suggested. External proxy routes not managed by LMS are outside this check."
                : level == AttackSurfaceFindingLevel.Protected
                    ? "Keep the existing MFA/passkey and route restrictions in place."
                    : "Validate only the pass-through identity provider and skipped upstream-certificate choices; their presence does not prove an unguarded route.");
    }

    private static AttackSurfaceFinding BuildIdentityFinding(
        SecuritySettingsPageViewModel security,
        IReadOnlyDictionary<Guid, int> passkeyCounts)
    {
        var enabled = security.Users.Where(user => user.IsEnabled).ToArray();
        var withOtp = enabled.Count(user => user.HasOtpSecret);
        var withPasskey = enabled.Count(user => passkeyCounts.GetValueOrDefault(user.Id) > 0);
        var withoutStrongAuthentication = enabled.Count(user =>
            !user.HasOtpSecret && passkeyCounts.GetValueOrDefault(user.Id) == 0);
        var level = enabled.Length == 0
            ? AttackSurfaceFindingLevel.Review
            : withoutStrongAuthentication > 0
                ? AttackSurfaceFindingLevel.Attention
                : AttackSurfaceFindingLevel.Protected;

        return new AttackSurfaceFinding(
            "identity",
            "Identity",
            "LMS operator authentication",
            level,
            enabled.Length == 0
                ? "No enabled LMS operator account was found."
                : withoutStrongAuthentication > 0
                    ? $"{withoutStrongAuthentication} enabled LMS account(s) have neither an authenticator secret nor a registered passkey."
                    : "Every enabled LMS account is protected by an authenticator or a passkey. Passkey-only accounts are correctly treated as strongly authenticated.",
            $"Enabled accounts: {enabled.Length}; with authenticator: {withOtp}; with passkey: {withPasskey}; without either: {withoutStrongAuthentication}; setup email delivery configured: {security.Messaging.CanSendLoginSetupEmail}.",
            level == AttackSurfaceFindingLevel.Protected
                ? "Keep recovery access controlled and disable accounts that are no longer used."
                : "Register an authenticator or passkey for each enabled account, or disable the account.");
    }

    private static AttackSurfaceFinding BuildTrustBoundaryFinding(SecuritySettingsPageViewModel security)
    {
        var enabled = security.TrustedNetworks.Where(network => network.IsEnabled).ToArray();
        var bypasses = enabled.Where(network => network.IsTrustedAccessEnabled && !network.IsAuthenticationEnabled).ToArray();
        return new AttackSurfaceFinding(
            "trusted-networks",
            "Trust boundaries",
            "Network-based authentication bypass",
            bypasses.Length > 0 ? AttackSurfaceFindingLevel.Attention : AttackSurfaceFindingLevel.Protected,
            bypasses.Length > 0
                ? $"{bypasses.Length} enabled network rule(s) trust source addresses without LMS authentication."
                : "No enabled trusted-network rule bypasses LMS authentication.",
            $"Enabled network rules: {enabled.Length}; trusted-access bypass rules: {bypasses.Length}.",
            "Keep bypass CIDRs narrow, account for proxies and forwarded addresses, and require authentication anywhere source-network identity is not a sufficient control.");
    }

    private static string FormatPorts(IReadOnlyList<FirewallListeningPortViewModel> listeners)
    {
        var values = listeners
            .Select(listener => $"{listener.Protocol}/{listener.Port}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        var suffix = listeners.Count > values.Length ? ", …" : string.Empty;
        return string.Join(", ", values) + suffix;
    }

    private static AttackSurfaceFinding BuildCloudflareTunnelFinding(AttackSurfaceFinding identityFinding) =>
        new(
            "cloudflare-tunnel",
            "External transport",
            "Outbound Cloudflare Tunnel",
            identityFinding.Level == AttackSurfaceFindingLevel.Protected
                ? AttackSurfaceFindingLevel.Protected
                : AttackSurfaceFindingLevel.Review,
            identityFinding.Level == AttackSurfaceFindingLevel.Protected
                ? "An active outbound cloudflared tunnel was observed, and LMS accounts have MFA/passkey protection. The tunnel does not require the LMS application port to be opened directly to the internet."
                : "An active outbound cloudflared tunnel was observed. Its outbound-only transport reduces direct origin exposure, while LMS account protection should be completed.",
            "cloudflared.service reported active. LMS can verify its own account controls but does not claim to have verified the external Cloudflare Access policy.",
            "Keep Cloudflare Access policy and LMS MFA/passkeys enabled as independent layers; no change is suggested from the tunnel itself.");

    private static AttackSurfacePosture BuildPosture(
        IReadOnlyList<AttackSurfaceFinding> findings,
        AttackSurfaceFinding firewallFinding,
        AttackSurfaceFinding remoteServiceFinding,
        AttackSurfaceFinding publishedRouteFinding,
        AttackSurfaceFinding identityFinding,
        bool? cloudflareTunnelActive)
    {
        var attentionCount = findings.Count(finding => finding.Level == AttackSurfaceFindingLevel.Attention);
        var reviewCount = findings.Count(finding => finding.Level == AttackSurfaceFindingLevel.Review);
        var level = attentionCount > 0
            ? AttackSurfaceFindingLevel.Attention
            : reviewCount > 0
                ? AttackSurfaceFindingLevel.Review
                : AttackSurfaceFindingLevel.Protected;
        var controls = new List<string>();
        if (firewallFinding.Level == AttackSurfaceFindingLevel.Protected)
        {
            controls.Add("Active deny-incoming host firewall; bound services are not automatically reachable.");
        }
        if (remoteServiceFinding.Level == AttackSurfaceFindingLevel.Protected &&
            remoteServiceFinding.Title.Contains("SSH", StringComparison.OrdinalIgnoreCase))
        {
            controls.Add("SSH administration is key-only for every enabled LMS-managed Linux account.");
        }
        if (identityFinding.Level == AttackSurfaceFindingLevel.Protected)
        {
            controls.Add("Every enabled LMS account has MFA or passkey authentication.");
        }
        if (publishedRouteFinding.Level == AttackSurfaceFindingLevel.Protected)
        {
            controls.Add("Enabled LMS-managed routes are strongly authenticated, blocked, or LAN-only.");
        }
        if (cloudflareTunnelActive == true)
        {
            controls.Add("An active outbound Cloudflare Tunnel reduces direct origin exposure.");
        }

        if (controls.Count == 0)
        {
            controls.Add("No strong control could be confirmed from the data available to this scan.");
        }

        return new AttackSurfacePosture(
            level,
            level == AttackSurfaceFindingLevel.Protected
                ? "Low observed exposure — strongly guarded"
                : level == AttackSurfaceFindingLevel.Review
                    ? "Low observed exposure — verify context"
                    : "A confirmed control gap needs attention",
            level switch
            {
                AttackSurfaceFindingLevel.Protected =>
                    "Within what LMS can verify, no unmitigated high-risk entry path was found. The observed controls materially reduce the residual risk.",
                AttackSurfaceFindingLevel.Review =>
                    $"No confirmed unguarded entry path was found. {reviewCount} item(s) need deployment context before LMS can call them guarded; this is uncertainty, not evidence of danger.",
                _ =>
                    $"LMS observed {attentionCount} concrete weakened-control condition(s). This identifies where to act, but does not mean exploitation or internet reachability has been proven."
            },
            controls,
            "This is a configuration-based residual-risk assessment, not a probability of compromise. External cloud firewall, router, VPN, and Cloudflare Access policies are not fully visible to the fixed scan and may reduce exposure further.");
    }

    private static IReadOnlyList<FirewallListeningPortViewModel> ResolveHostFirewallPermittedListeners(
        FirewallStatusViewModel firewall)
    {
        if (!HasDefaultDenyFirewall(firewall))
        {
            return firewall.ListeningPorts;
        }

        var allowRules = firewall.Rules
            .Where(rule => rule.IsEnabled &&
                           (rule.Action.StartsWith("ALLOW", StringComparison.OrdinalIgnoreCase) ||
                            rule.Action.StartsWith("LIMIT", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return firewall.ListeningPorts
            .Where(listener => allowRules.Any(rule => RulePermits(rule, listener)))
            .ToArray();
    }

    private static bool RulePermits(
        FirewallRuleViewModel rule,
        FirewallListeningPortViewModel listener)
    {
        if (rule.EditableAllowRule is { } editor)
        {
            var protocolMatches = editor.Protocol == FirewallProtocol.Any ||
                                  editor.Protocol.ToString().Equals(listener.Protocol, StringComparison.OrdinalIgnoreCase);
            return protocolMatches && PortExpressionContains(editor.Port, listener.Port);
        }

        var destination = rule.Destination.Trim();
        if (destination.Equals("Anywhere", StringComparison.OrdinalIgnoreCase) ||
            destination.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (destination.Contains("OpenSSH", StringComparison.OrdinalIgnoreCase) ||
            destination.Equals("SSH", StringComparison.OrdinalIgnoreCase))
        {
            return listener.Port == 22 && listener.Protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase);
        }

        foreach (var token in destination.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split('/', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 &&
                !parts[1].Equals(listener.Protocol, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (PortExpressionContains(parts[0], listener.Port))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PortExpressionContains(string expression, int port)
    {
        if (int.TryParse(expression, out var exact))
        {
            return exact == port;
        }

        var bounds = expression.Split([':', '-'], 2, StringSplitOptions.TrimEntries);
        return bounds.Length == 2 &&
               int.TryParse(bounds[0], out var start) &&
               int.TryParse(bounds[1], out var end) &&
               port >= start &&
               port <= end;
    }

    private static bool HasDefaultDenyFirewall(FirewallStatusViewModel firewall) =>
        firewall.IsInstalled &&
        firewall.CanManage &&
        firewall.IsActive &&
        firewall.IncomingPolicy.Equals("deny", StringComparison.OrdinalIgnoreCase);
}
