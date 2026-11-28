// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Net;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using Microsoft.Extensions.Caching.Memory;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class PublicIpAddressDiscoveryService(HttpClient http, IMemoryCache cache) : IPublicIpAddressDiscoveryService
{
    private const string CacheKey = "lms-server-public-egress-ip-v1";
    public async Task<PublicIpAddressDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue<PublicIpAddressDiscoveryResult>(CacheKey, out var cached) && cached is not null) return cached;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var lookupToken = timeout.Token;
        PublicIpAddressDiscoveryResult result;
        try
        {
            // This request originates on the LMS server, not the operator's browser.
            using var response = await http.GetAsync("https://www.cloudflare.com/cdn-cgi/trace", HttpCompletionOption.ResponseHeadersRead, lookupToken);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(16384, lookupToken);
            var text = await response.Content.ReadAsStringAsync(lookupToken);
            var candidates = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith("ip=", StringComparison.Ordinal)).Select(line => line[3..]).ToArray();
            result = candidates.Length == 1 && PublicIpAddressRules.IsPublic(candidates[0])
                ? new(true, IPAddress.Parse(candidates[0]).ToString(), "The server's public egress IP was detected. SSH reachability has not been tested.")
                : new(false, "", "The public-IP service did not return a usable public address. Local interface addresses are still available.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { result = new(false, "", "The public-IP lookup timed out. Local interface addresses are still available."); }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        { result = new(false, "", "The public-IP service could not be reached. Local interface addresses are still available."); }
        cache.Set(CacheKey, result, result.IsAvailable ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(15));
        return result;
    }
}
