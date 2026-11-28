// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Application.Contracts.EdgeGateway;
using LinuxMadeSane.Application.Services;
using LinuxMadeSane.Core.Models.Cloudflare;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.EdgeGateway;

namespace LinuxMadeSane.Application.Services.EdgeGateway;

public sealed partial class EdgeGatewayService
{
    public async Task<EdgeGatewayRouteHostHeaderCheck> CheckRouteHostHeaderAsync(
        EdgeGatewayRouteEditor editor, CancellationToken cancellationToken = default)
    {
        // Preserve explicit upstream headers and source-bound routes. A change is
        // made only for an identified hostname rejection with a working alternative.
        if (proxyCompatibility is null || !editor.UsePublicHostHeader || !string.IsNullOrEmpty(editor.UpstreamSourceAddress))
            return new(editor.UsePublicHostHeader, 0, false, string.Empty);
        var domain = EdgeGatewayRouteValidator.NormalizeDomainName(editor.DomainName);
        var hostname = NormalizeHostnameOrSubdomain(editor.Hostname, domain);
        var host = EdgeGatewayRouteValidator.NormalizeTargetHost(editor.TargetHost);
        var port = EdgeGatewayRouteValidator.NormalizeTargetPort(editor.TargetPort);
        var scheme = editor.TargetScheme == LinuxMadeSane.Core.Enums.EdgeGatewayTargetScheme.Https ? "https" : "http";
        var endpoint = new LocalHttpServiceEndpoint(new UriBuilder(scheme, host, port).Uri.AbsoluteUri, scheme, host, port, 0, null, null);
        var preferences = new OnDemandAppProxyPreferences(
            Scheme: scheme == "https" ? OnDemandAppSchemePreference.Https : OnDemandAppSchemePreference.Http,
            HostHeader: OnDemandAppHostHeaderPreference.Public,
            ForwardedFor: editor.StripForwardedFor ? OnDemandAppForwardedForPreference.Strip : OnDemandAppForwardedForPreference.Preserve);
        var publicProfile = await proxyCompatibility.TestAsync(endpoint, hostname, preferences, cancellationToken);
        if (!publicProfile.HostnameRejected)
            return new(true, publicProfile.Endpoint.StatusCode, false, publicProfile.SelectionReason);
        var upstream = await proxyCompatibility.TestAsync(endpoint, hostname,
            preferences with { HostHeader = OnDemandAppHostHeaderPreference.Upstream }, cancellationToken);
        if (upstream.Endpoint.StatusCode is >= 200 and < 400 or 401 or 403)
            return new(false, upstream.Endpoint.StatusCode, false,
                "The application rejects the public Host header. LMS will use the internal address for Host and preserve the public URL in X-Forwarded-Host.");
        return new(true, publicProfile.Endpoint.StatusCode, true,
            "The application rejects this public hostname and did not accept its internal Host header. Configure an accepted application hostname before saving; no route was changed.");
    }

    public async Task<EdgeGatewayRouteMappingReview> InspectRouteMappingAsync(
        EdgeGatewayRouteEditor editor, CancellationToken cancellationToken = default) =>
        (await ReadRouteMappingSnapshotAsync(editor, cancellationToken)).Review;

    public async Task<EdgeGatewayCloudflareRouteSetupResult> SaveAndPublishRouteAsync(
        EdgeGatewayRouteEditor editor, string reviewToken, bool overwriteConfirmed,
        CancellationToken cancellationToken = default)
    {
        editor = JsonSerializer.Deserialize<EdgeGatewayRouteEditor>(JsonSerializer.Serialize(editor))!;
        var snapshot = await ReadRouteMappingSnapshotAsync(editor, cancellationToken);
        if (snapshot.Review.ReviewToken != reviewToken)
            throw new InvalidOperationException("The hostname, destination or existing mappings changed. Review them again before saving; no mappings were changed.");
        if (snapshot.Review.HasConflict && !overwriteConfirmed)
            throw new InvalidOperationException($"{snapshot.Review.Hostname} already exists. Confirm overwriting its mappings before saving; nothing was changed.");
        if (!editor.Enabled)
            throw new InvalidOperationException("Enable the service before replacing a published mapping.");

        var headerCheck = await CheckRouteHostHeaderAsync(editor, cancellationToken);
        if (headerCheck.HostnameRejected) throw new InvalidOperationException(headerCheck.Summary);
        var hostHeaderChanged = editor.UsePublicHostHeader != headerCheck.UsePublicHostHeader;
        editor.UsePublicHostHeader = headerCheck.UsePublicHostHeader;

        // Validate the full replacement before removing any existing local mapping.
        var originalEdit = editor.Id is { } editId ? await store.GetRouteAsync(editId, cancellationToken) : null;
        var id = await SaveRouteCoreAsync(editor, overwriteConfirmed, cancellationToken);
        if (overwriteConfirmed)
        {
            foreach (var previous in snapshot.LocalRoutes.Where(route => route.Id != id))
                await store.DeleteRouteAsync(previous.Id, cancellationToken);
        }
        async Task RestoreLocalMappingsAsync()
        {
            await store.DeleteRouteAsync(id, CancellationToken.None);
            foreach (var previous in snapshot.LocalRoutes) await store.SaveRouteAsync(previous, CancellationToken.None);
            if (originalEdit is not null) await store.SaveRouteAsync(originalEdit, CancellationToken.None);
        }
        EdgeGatewayCloudflareRouteSetupResult result;
        try
        {
            result = await ProvisionCloudflareRouteCoreAsync(id, overwriteConfirmed, snapshot.AddressRecords, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await RestoreLocalMappingsAsync();
            throw;
        }
        catch (Exception exception)
        {
            await RestoreLocalMappingsAsync();
            throw new InvalidOperationException($"Publishing did not finish. Previous local mappings were restored. Cloudflare may have partial changes; review this hostname again before retrying. {exception.Message}", exception);
        }
        if (!result.Success)
        {
            await RestoreLocalMappingsAsync();
            return result with { Summary = result.Summary + " Previous local mappings were restored. Review any Cloudflare changes before retrying." };
        }

        if (hostHeaderChanged) result = result with { Summary = result.Summary + " " + headerCheck.Summary };

        // DNS now sends this hostname through the selected relay. Retire only its
        // exact ingress on the other remotely managed tunnels, retaining wildcards.
        foreach (var previous in snapshot.OtherIngress)
        {
            try
            {
                var current = await cloudflareTunnelService.GetConfigurationAsync(snapshot.Token, snapshot.AccountId, previous.TunnelId, cancellationToken);
                var currentMatching = current.Routes.Where(route => route.Hostname.Equals(snapshot.Review.Hostname, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (JsonSerializer.Serialize(currentMatching) != JsonSerializer.Serialize(previous.Routes))
                    return result with { Success = false, Summary = "The new mapping is live, but a previous tunnel mapping changed during replacement and was preserved. Review the hostname again to finish cleanup." };
                var updated = current.Routes.Where(route => !route.Hostname.Equals(snapshot.Review.Hostname, StringComparison.OrdinalIgnoreCase)).ToList();
                if (updated.All(route => !string.IsNullOrWhiteSpace(route.Hostname))) updated.Add(new("", "http_status:404"));
                await cloudflareTunnelService.UpdateConfigurationAsync(snapshot.Token, snapshot.AccountId, previous.TunnelId, new(updated), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return result with { Success = false, Summary = $"The new mapping is live, but the previous ingress on tunnel {previous.TunnelId} could not be removed: {exception.Message}. Review and save again to finish cleanup." };
            }
        }
        return result;
    }

    private async Task<RouteMappingSnapshot> ReadRouteMappingSnapshotAsync(EdgeGatewayRouteEditor editor, CancellationToken cancellationToken)
    {
        // Capture the edit before awaiting network calls; a newer keystroke must not
        // reuse a review that was made for another hostname or target.
        var editJson = JsonSerializer.Serialize(editor);
        editor = JsonSerializer.Deserialize<EdgeGatewayRouteEditor>(editJson)!;
        var domain = EdgeGatewayRouteValidator.NormalizeDomainName(editor.DomainName);
        var hostname = NormalizeHostnameOrSubdomain(editor.Hostname, domain);
        if (!hostname.Equals(domain, StringComparison.OrdinalIgnoreCase) && !hostname.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The route hostname must sit inside the selected domain.");
        var settings = await GetGatewaySettingsAsync(cancellationToken);
        var gateway = BuildGatewayDomainName(domain, settings.GatewaySubdomain);
        var relayHostname = $"{ResolveRelativeHostname(hostname, domain)}.{gateway}";
        var token = await ResolveSavedCloudflareTokenAsync(cancellationToken);
        var validation = await exposedServiceManager.ValidateTokenAsync(AiLocalMachine.ManagedHostId, null, cancellationToken);
        var zone = ResolveCleanupZone(validation.Zones, domain);
        var account = ResolveCloudflareAccount(validation.Accounts, zone)
            ?? throw new InvalidOperationException("Cloudflare did not return the account that owns this zone.");
        var records = await cloudflareDnsService.ListRecordsAsync(token, zone.Id, cancellationToken);
        var wildcard = records.FirstOrDefault(record => IsWildcardRecordForDomain(record, gateway));
        var tunnels = await cloudflareTunnelService.ListTunnelsAsync(token, account.Id, cancellationToken);
        var tunnelId = TryExtractTunnelIdFromDnsTarget(wildcard?.Content);
        var selectedTunnel = tunnels.FirstOrDefault(tunnel => !tunnel.IsDeleted && tunnel.Id.Equals(tunnelId, StringComparison.OrdinalIgnoreCase));
        var connector = await TryInspectLocalCloudflaredConnectorAsync(cancellationToken);
        if (wildcard?.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase) != true || !wildcard.Proxied ||
            !IsRelayTunnelOwnedByThisLms(selectedTunnel, gateway, settings.TunnelInstanceId, connector))
            throw new InvalidOperationException($"Set up and verify this host's relay for {domain} before replacing a public mapping. Existing DNS was preserved.");
        if (selectedTunnel!.ConfigSource != "cloudflare" ||
            connector is { IsInstalled: true } && !string.IsNullOrWhiteSpace(connector.TunnelId) && connector.TunnelId != selectedTunnel.Id)
            throw new InvalidOperationException("The selected relay is not the active remotely managed connector. Set up this relay before replacing a public mapping; existing mappings were preserved.");
        var local = (await store.ListRoutesAsync(cancellationToken)).Where(route => route.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase)).OrderBy(route => route.Id).ToArray();
        var dns = records.Where(record => IsRouteAddressRecord(record, hostname)).OrderBy(record => record.Id, StringComparer.Ordinal).ToArray();
        var ingress = new List<PreviousHostnameIngress>();
        foreach (var tunnel in tunnels.Where(tunnel => !tunnel.IsDeleted && tunnel.ConfigSource.Equals("cloudflare", StringComparison.OrdinalIgnoreCase)).OrderBy(tunnel => tunnel.Id, StringComparer.Ordinal))
        {
            var config = await cloudflareTunnelService.GetConfigurationAsync(token, account.Id, tunnel.Id, cancellationToken);
            var matches = config.Routes.Where(route => route.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 0) ingress.Add(new(tunnel.Id, matches));
        }
        var original = editor.Id is { } editedId ? await store.GetRouteAsync(editedId, cancellationToken) : null;
        var editingOwnHostname = original is not null && original.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase) &&
            original.DomainName.Equals(domain, StringComparison.OrdinalIgnoreCase);
        var desiredPath = EdgeGatewayRouteValidator.NormalizePathPrefix(editor.TargetPathPrefix);
        var conflictingLocal = local.Where(route => route.Id != editor.Id &&
            (!editingOwnHostname || EdgeGatewayRouteValidator.NormalizePathPrefix(route.TargetPathPrefix).Equals(desiredPath, StringComparison.OrdinalIgnoreCase)));
        var conflictingDns = dns.Where(record => !editingOwnHostname || !IsSameDnsTarget(record, relayHostname));
        var conflictingIngress = ingress.Where(item => !editingOwnHostname || item.TunnelId != selectedTunnel.Id ||
            item.Routes.Any(route => !route.Service.TrimEnd('/').Equals(ResolveCaddyServiceUrl().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)));
        var hasConflict = conflictingLocal.Any() || conflictingDns.Any() || conflictingIngress.Any();
        var descriptions = hasConflict
            ? local.Where(route => route.Id != editor.Id).Select(route => $"LMS: {route.DisplayName} ({route.Hostname}{route.TargetPathPrefix})")
                .Concat(dns.Select(record => $"DNS: {record.Type} {record.Name} → {record.Content}"))
                .Concat(ingress.Select(item => $"Tunnel {item.TunnelId}: {hostname}")).ToArray()
            : [];
        var state = JsonSerializer.Serialize(new { editJson, settings.GatewaySubdomain, settings.TunnelInstanceId, wildcard, original, local, dns, ingress });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
        return new(new(hostname, relayHostname, hash, descriptions), token, account.Id, local, dns,
            ingress.Where(item => item.TunnelId != selectedTunnel!.Id).ToArray());
    }

    private static bool IsRouteAddressRecord(CloudflareDnsRecord record, string hostname) =>
        NormalizeDnsHostname(record.Name).Equals(hostname, StringComparison.OrdinalIgnoreCase) &&
        (record.Type.Equals("A", StringComparison.OrdinalIgnoreCase) || record.Type.Equals("AAAA", StringComparison.OrdinalIgnoreCase) || record.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase));

    private sealed record PreviousHostnameIngress(string TunnelId, IReadOnlyList<CloudflareTunnelRoute> Routes);
    private sealed record RouteMappingSnapshot(EdgeGatewayRouteMappingReview Review, string Token, string AccountId,
        IReadOnlyList<EdgeGatewayRoute> LocalRoutes, IReadOnlyList<CloudflareDnsRecord> AddressRecords, IReadOnlyList<PreviousHostnameIngress> OtherIngress);
}
