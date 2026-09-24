// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Cloudflare;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalHttpServiceProxyCompatibilityService(
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<LocalHttpServiceProxyCompatibilityService> logger,
    OnDemandAppSourceBinding sourceBinding) : ILocalHttpServiceProxyCompatibilityService
{
    internal const string HttpClientName = "LocalHttpServiceProxyCompatibility";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(3);
    private readonly ConcurrentDictionary<string, CacheEntry> cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<LocalHttpServiceProxyProfile> SelectAsync(
        LocalHttpServiceEndpoint endpoint,
        string publicHostname,
        OnDemandAppProxyPreferences preferences,
        CancellationToken cancellationToken = default) =>
        SelectCoreAsync(endpoint, publicHostname, preferences, false, cancellationToken);

    public Task<LocalHttpServiceProxyProfile> TestAsync(
        LocalHttpServiceEndpoint endpoint,
        string publicHostname,
        OnDemandAppProxyPreferences preferences,
        CancellationToken cancellationToken = default) =>
        SelectCoreAsync(endpoint, publicHostname, preferences, true, cancellationToken);

    private async Task<LocalHttpServiceProxyProfile> SelectCoreAsync(
        LocalHttpServiceEndpoint endpoint,
        string publicHostname,
        OnDemandAppProxyPreferences preferences,
        bool forceTest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicHostname);
        ArgumentNullException.ThrowIfNull(preferences);

        endpoint = await sourceBinding.ValidateAsync(endpoint, preferences, cancellationToken);
        var key = BuildCacheKey(endpoint, preferences);
        using var boundClient = preferences.ConnectAsLms
            ? OnDemandAppSourceBinding.CreateProbeClient(preferences.SourceAddress)
            : null;
        if (!forceTest && !preferences.ConnectAsLms && cache.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > timeProvider.GetUtcNow())
        {
            return cached.Profile;
        }

        var candidates = BuildCandidates(endpoint, preferences).ToArray();
        var probes = await Task.WhenAll(candidates.Select(candidate =>
            ProbeAsync(endpoint, publicHostname.Trim(), candidate, boundClient, cancellationToken)));
        var selected = probes
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Candidate.Order)
            .First();

        LocalHttpServiceProxyProfile profile;
        if (!selected.Responded && preferences.ConnectAsLms)
        {
            throw new InvalidOperationException($"No HTTP response from {endpoint.Host} using {preferences.SourceInterface} ({preferences.SourceAddress}). No route was created.");
        }

        if (!selected.Responded)
        {
            var fallbackEndpoint = endpoint with
            {
                Scheme = selected.Candidate.Scheme,
                Port = selected.Candidate.Port,
                Url = BuildEndpointUrl(selected.Candidate.Scheme, endpoint.Host, selected.Candidate.Port)
            };
            profile = new LocalHttpServiceProxyProfile(
                fallbackEndpoint,
                selected.Candidate.UsePublicHostHeader,
                selected.Candidate.StripForwardedFor,
                "No compatibility probe received an HTTP response; retained the selected favourite settings.");
            logger.LogWarning(
                "No proxy compatibility probe succeeded for discovered endpoint {EndpointHost}:{EndpointPort}; retaining discovered scheme and conservative headers.",
                endpoint.Host,
                endpoint.Port);
        }
        else
        {
            var selectedEndpoint = endpoint with
            {
                Scheme = selected.Candidate.Scheme,
                Port = selected.Candidate.Port,
                Url = BuildEndpointUrl(selected.Candidate.Scheme, endpoint.Host, selected.Candidate.Port)
            };
            profile = new LocalHttpServiceProxyProfile(
                selectedEndpoint,
                selected.Candidate.UsePublicHostHeader,
                selected.Candidate.StripForwardedFor,
                $"Compatibility probe returned HTTP {selected.StatusCode}.");
            logger.LogInformation(
                "Selected proxy compatibility profile for {EndpointHost}:{EndpointPort}: scheme={Scheme}, port={SelectedPort}, publicHostHeader={UsePublicHostHeader}, stripForwardedFor={StripForwardedFor}, status={StatusCode}.",
                endpoint.Host,
                endpoint.Port,
                selected.Candidate.Scheme,
                selected.Candidate.Port,
                selected.Candidate.UsePublicHostHeader,
                selected.Candidate.StripForwardedFor,
                selected.StatusCode);
        }

        cache[key] = new CacheEntry(profile, timeProvider.GetUtcNow().Add(CacheLifetime));
        return profile;
    }

    private async Task<ProbeResult> ProbeAsync(
        LocalHttpServiceEndpoint endpoint,
        string publicHostname,
        ProbeCandidate candidate,
        HttpClient? boundClient,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                BuildEndpointUrl(candidate.Scheme, endpoint.Host, candidate.Port));
            request.Headers.Host = candidate.UsePublicHostHeader
                ? publicHostname
                : FormatHostHeader(endpoint.Host, candidate.Port);
            request.Headers.UserAgent.ParseAdd("LinuxMadeSane-OnDemand-Probe/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
            request.Headers.TryAddWithoutValidation("X-Forwarded-Host", publicHostname);
            request.Headers.TryAddWithoutValidation("X-Forwarded-Port", "443");
            if (!candidate.StripForwardedFor)
            {
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", "192.0.2.1");
            }

            using var response = await (boundClient ?? httpClientFactory.CreateClient(HttpClientName)).SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            var contentSample = await ReadResponseSampleAsync(response, timeout.Token);
            return new ProbeResult(
                candidate,
                true,
                (int)response.StatusCode,
                Score(response, contentSample, candidate, endpoint, publicHostname));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeResult.Failed(candidate);
        }
        catch (HttpRequestException)
        {
            return ProbeResult.Failed(candidate);
        }
    }

    private static IEnumerable<ProbeCandidate> BuildCandidates(
        LocalHttpServiceEndpoint endpoint,
        OnDemandAppProxyPreferences preferences)
    {
        var discoveredScheme = endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? Uri.UriSchemeHttps
            : Uri.UriSchemeHttp;
        var alternateScheme = discoveredScheme == Uri.UriSchemeHttps ? Uri.UriSchemeHttp : Uri.UriSchemeHttps;
        string[] schemes = preferences.Scheme switch
        {
            OnDemandAppSchemePreference.Http => [Uri.UriSchemeHttp],
            OnDemandAppSchemePreference.Https => [Uri.UriSchemeHttps],
            _ => new[] { discoveredScheme, alternateScheme }
        };
        bool[] hostHeaders = preferences.HostHeader switch
        {
            OnDemandAppHostHeaderPreference.Upstream => [false],
            OnDemandAppHostHeaderPreference.Public => [true],
            _ => new[] { false, true }
        };
        bool[] forwardedForModes = preferences.ConnectAsLms ? [true] : preferences.ForwardedFor switch
        {
            OnDemandAppForwardedForPreference.Strip => [true],
            OnDemandAppForwardedForPreference.Preserve => [false],
            _ => new[] { true, false }
        };
        var order = 0;
        foreach (var scheme in schemes)
        {
            var port = ResolvePort(endpoint, scheme);
            foreach (var stripForwardedFor in forwardedForModes)
            {
                foreach (var usePublicHostHeader in hostHeaders)
                {
                    yield return new ProbeCandidate(
                        scheme,
                        port,
                        usePublicHostHeader,
                        stripForwardedFor,
                        scheme == discoveredScheme,
                        order++);
                }
            }
        }
    }

    private static int Score(
        HttpResponseMessage response,
        string contentSample,
        ProbeCandidate candidate,
        LocalHttpServiceEndpoint endpoint,
        string publicHostname)
    {
        var status = (int)response.StatusCode;
        var score = status switch
        {
            >= 200 and <= 299 => 1_000,
            >= 300 and <= 399 => 800,
            401 or 403 => 700,
            >= 400 and <= 499 => 500,
            >= 500 and <= 599 => 300,
            _ => 100
        };

        if (!string.IsNullOrWhiteSpace(contentSample))
        {
            score += 20;
        }

        var title = TryReadHtmlTitle(contentSample);
        if (!string.IsNullOrWhiteSpace(title))
        {
            score += LocalHttpServiceDiscoveryRanking.LooksLikeErrorTitle(title) ? -250 : 50;
        }

        // When responses are equivalent, prefer the least surprising proxy behaviour.
        if (candidate.IsDiscoveredScheme)
        {
            score += 8;
        }

        if (candidate.StripForwardedFor)
        {
            score += 4;
        }

        if (!candidate.UsePublicHostHeader)
        {
            score += 2;
        }

        if (status is >= 300 and <= 399 && response.Headers.Location is { } location)
        {
            if (!location.IsAbsoluteUri ||
                location.Host.Equals(publicHostname, StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
            }
            else if (location.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase))
            {
                // An internal absolute redirect would send the browser around the
                // authenticated public route. Prefer a profile that stays proxied.
                score -= 400;
            }
        }

        return score;
    }

    private static int ResolvePort(LocalHttpServiceEndpoint endpoint, string scheme)
    {
        if (scheme.Equals(endpoint.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return endpoint.Port;
        }

        return (endpoint.Scheme.ToLowerInvariant(), endpoint.Port, scheme.ToLowerInvariant()) switch
        {
            ("http", 80, "https") => 443,
            ("https", 443, "http") => 80,
            _ => endpoint.Port
        };
    }

    private static async Task<string> ReadResponseSampleAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        const int maximumBytes = 32 * 1024;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[maximumBytes];
        var bytesRead = 0;
        while (bytesRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(bytesRead, buffer.Length - bytesRead), cancellationToken);
            if (read == 0)
            {
                break;
            }

            bytesRead += read;
        }

        return bytesRead == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, bytesRead);
    }

    private static string? TryReadHtmlTitle(string html)
    {
        var opening = html.IndexOf("<title", StringComparison.OrdinalIgnoreCase);
        if (opening < 0)
        {
            return null;
        }

        var contentStart = html.IndexOf('>', opening);
        if (contentStart < 0)
        {
            return null;
        }

        var closing = html.IndexOf("</title>", contentStart + 1, StringComparison.OrdinalIgnoreCase);
        if (closing < 0)
        {
            return null;
        }

        return WebUtility.HtmlDecode(html[(contentStart + 1)..closing]).Trim();
    }

    private static string BuildEndpointUrl(string scheme, string host, int port) =>
        new UriBuilder(scheme, host, port, "/").Uri.AbsoluteUri;

    private static string FormatHostHeader(string host, int port)
    {
        var formattedHost = host.Contains(':') && !host.StartsWith("[", StringComparison.Ordinal)
            ? $"[{host}]"
            : host;
        return $"{formattedHost}:{port}";
    }

    private static string BuildCacheKey(
        LocalHttpServiceEndpoint endpoint,
        OnDemandAppProxyPreferences preferences) =>
        string.Join('|',
            LocalHttpServiceDiscoveryRanking.StableKey(endpoint),
            endpoint.Host.Trim().ToLowerInvariant(),
            endpoint.Scheme.Trim().ToLowerInvariant(),
            preferences.TargetAddress,
            preferences.Scheme,
            preferences.HostHeader,
            preferences.ForwardedFor,
            preferences.ConnectAsLms,
            preferences.SourceInterface,
            preferences.SourceAddress);

    private sealed record CacheEntry(LocalHttpServiceProxyProfile Profile, DateTimeOffset ExpiresAtUtc);
    private sealed record ProbeCandidate(
        string Scheme,
        int Port,
        bool UsePublicHostHeader,
        bool StripForwardedFor,
        bool IsDiscoveredScheme,
        int Order);
    private sealed record ProbeResult(
        ProbeCandidate Candidate,
        bool Responded,
        int StatusCode,
        int Score)
    {
        public static ProbeResult Failed(ProbeCandidate candidate) =>
            new(candidate, false, 0, int.MinValue);
    }
}
