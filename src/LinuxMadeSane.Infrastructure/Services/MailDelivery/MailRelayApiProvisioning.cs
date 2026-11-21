// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Text.Json;
using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Core.Models.MailRelay;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed partial class MailRelayProvisioningService
{
    private async Task<MailRelaySetupPreview> PreviewApiAsync(MailRelaySetupRequest request, CancellationToken token)
    {
        var errors = Validate(request).ToList();
        var outbound = request.Outbound!;
        if (HttpsOutboundMailProvider.Validate(outbound) is { } error) errors.Add(error);
        if (!outbound.Api.SenderAddress.EndsWith("@" + request.SendingDomain, StringComparison.OrdinalIgnoreCase))
            errors.Add("The sending domain must match the authorised provider mailbox.");
        if (request.LocalDevices is { } devices)
        {
            try { request = request with { LocalDevices = NormalizeLegacyRequest(devices) }; }
            catch (InvalidOperationException exception) { errors.Add(exception.Message); }
        }
        var installed = await store.GetConfigurationAsync(token);
        if (installed?.Enabled == true && installed.RelayHostname != request.RelayHostname)
            errors.Add("Keep the existing relay hostname when changing delivery providers.");
        var preflight = await preflightService.InspectAsync(true, cancellationToken: token);
        var validation = HttpsOutboundMailProvider.Validate(outbound);
        var check = new MailRelayPreflightCheck(MailRelayPreflightCheckKeys.OutboundSmtp, "HTTPS delivery",
            validation is null ? MailRelayPreflightCheckState.Pass : MailRelayPreflightCheckState.Failed,
            "HTTPS 443", validation ?? $"{outbound.Mode} accepts messages over HTTPS. Sender authorisation must be verified with a test.");
        preflight = preflight with { HttpsDelivery = true, Checks = preflight.Checks.Select(c => c.Key == check.Key ? check : c).ToArray() };
        if (!preflight.DockerCanBePrepared) errors.Add("Docker must be available to run the existing SMTP listener.");
        request = request with { Outbound = outbound with { DestinationPort = 443 } };
        return new(request, preflight, [], new(request.SendingDomain, MailRelayExistingProvider.NoneDetected, [], null, "", 0, [], null, null, MailRelayDeliveryMode.DirectInternet),
            ["The provider must authorise this sender and perform DKIM signing. Existing mail DNS is preserved. API acceptance does not confirm recipient delivery.",
             "The private SMTP listener and its source restrictions are unchanged. Applications use the existing queue."], errors)
        { OutboundSummary = $"{outbound.Mode} · HTTPS 443" };
    }
    private async Task<MailRelayConfiguration> ClearApiCredentialsAsync(MailRelayConfiguration configuration, CancellationToken token)
    {
        var profiles = new List<MailRelayApiSettings> { configuration.Outbound.Api };
        if (providerDrafts is not null)
            foreach (var mode in new[] { MailRelayOutboundMode.MicrosoftGraph, MailRelayOutboundMode.Gmail, MailRelayOutboundMode.AmazonSes })
            {
                if (await providerDrafts.GetAsync(mode, token) is { } draft) profiles.Add(draft.Api);
                await providerDrafts.ClearAsync(mode, token);
            }
        if (providerConnections is not null)
        {
            foreach (var connection in await providerConnections.ListAsync(token)) profiles.Add(connection.Settings.Api);
            await providerConnections.ClearAsync(token);
        }
        foreach (var reference in profiles.SelectMany(p => new[] { p.ClientSecretReference, p.CertificateSecretReference, p.RefreshTokenReference,
                     p.ServiceAccountSecretReference, p.AwsCredentialsSecretReference, p.BridgeSecretReference }).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct())
            await secretStore.DeleteSecretAsync(reference!, token);
        await RunRequiredAsync(new LinuxMadeSane.Core.Models.RdpOptimizer.LinuxCommandRequest("rm", ["-f", "--", InstallRoot + "/config/lms-api.json"], true,
            TimeSpan.FromSeconds(20), "Remove local HTTPS delivery runtime credentials"), token);
        return configuration with { Outbound = configuration.Outbound with { Api = new() } };
    }
    private async Task RequireEmptyQueueForProviderChangeAsync(MailRelayConfiguration installed, MailRelayOutboundSettings requested, CancellationToken token)
    {
        if (!installed.Enabled || !(installed.Outbound.UsesHttpsApi || requested.UsesHttpsApi) ||
            installed.Outbound.Mode == requested.Mode && installed.Outbound.Api.SenderAddress.Equals(requested.Api.SenderAddress, StringComparison.OrdinalIgnoreCase)) return;
        var queue = await commandRunner.RunAsync(new LinuxMadeSane.Core.Models.RdpOptimizer.LinuxCommandRequest("docker", ["exec", "lms-mail-relay", "postqueue", "-j"], true,
            TimeSpan.FromSeconds(20), "Check the existing mail queue before changing delivery provider"), false, token);
        if (queue.ExitCode != 0 || !string.IsNullOrWhiteSpace(queue.StandardOutput))
            throw new InvalidOperationException("The delivery provider or sender cannot change while messages are queued. Resolve or drain the existing queue first; changing credentials for the same provider and sender remains available.");
    }
    private async Task QuiesceProviderTransitionAsync(MailRelayConfiguration installed, MailRelayOutboundSettings requested, CancellationToken token)
    {
        if (!installed.Enabled || !(installed.Outbound.UsesHttpsApi || requested.UsesHttpsApi) ||
            installed.Outbound.Mode == requested.Mode && installed.Outbound.Api.SenderAddress.Equals(requested.Api.SenderAddress, StringComparison.OrdinalIgnoreCase)) return;
        // Stop acceptance and delivery before the final empty-queue check. A preflight
        // check alone races new SMTP submissions during image/config preparation.
        const string script = """
            set -eu
            postfix stop
            trap 'postfix start >/dev/null 2>&1 || true' EXIT
            queued=$(find /var/spool/postfix/active /var/spool/postfix/deferred /var/spool/postfix/incoming /var/spool/postfix/maildrop /var/spool/postfix/hold -type f -print -quit)
            if [ -n "$queued" ]; then exit 75; fi
            trap - EXIT
            """;
        var result = await commandRunner.RunAsync(new LinuxMadeSane.Core.Models.RdpOptimizer.LinuxCommandRequest("docker", ["exec", "lms-mail-relay", "sh", "-ceu", script], true,
            TimeSpan.FromSeconds(30), "Pause SMTP and verify the queue is empty before switching delivery provider"), false, token);
        if (result.ExitCode != 0) throw new InvalidOperationException("LMS could not confirm an empty queue for this provider change. If new mail arrived, SMTP was resumed on the previous route. Pause sending applications and drain the queue, then review the existing relay status before retrying.");
    }
    private async Task<string> BuildBridgeConfigurationAsync(MailRelayConfiguration configuration, CancellationToken token)
    {
        if (!configuration.Outbound.UsesHttpsApi) return "{}";
        var reference = configuration.Outbound.Api.BridgeSecretReference;
        var secret = string.IsNullOrWhiteSpace(reference) ? null : await secretStore.ResolveSecretAsync(reference, token);
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("The local mail-delivery credential is missing. Run relay setup again to recreate it.");
        return JsonSerializer.Serialize(new { token = secret, endpoint = "http://127.0.0.1:5080/internal/mail-relay/deliver" });
    }
    private static string ReadApiTransport()
    {
        using var stream = typeof(MailRelayProvisioningService).Assembly.GetManifestResourceStream("LinuxMadeSane.Infrastructure.Services.MailDelivery.postfix-api.py")
            ?? throw new InvalidOperationException("The native mail-delivery adapter is missing from this build.");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
}
