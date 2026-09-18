// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net.Http.Json;
using System.Text.Json.Serialization;
using LinuxMadeSane.Application.Interfaces;

namespace LinuxMadeSane.Infrastructure.Services.Cloudflare;

public sealed class PublicDnsPropagationService(
    HttpClient httpClient,
    TimeProvider timeProvider) : IPublicDnsPropagationService
{
    private static readonly string[] ResolverEndpoints =
    [
        "https://cloudflare-dns.com/dns-query",
        "https://dns.google/resolve"
    ];

    public async Task<bool> WaitUntilResolvableAsync(
        string hostname,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var normalizedHostname = hostname.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(normalizedHostname))
        {
            return false;
        }

        var deadline = timeProvider.GetUtcNow().Add(timeout);
        await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken);
        while (timeProvider.GetUtcNow() < deadline)
        {
            foreach (var endpoint in ResolverEndpoints)
            {
                if (await ResolvesAsync(endpoint, normalizedHostname, cancellationToken))
                {
                    return true;
                }
            }

            var remaining = deadline - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(
                remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1),
                timeProvider,
                cancellationToken);
        }

        return false;
    }

    private async Task<bool> ResolvesAsync(
        string endpoint,
        string hostname,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{endpoint}?name={Uri.EscapeDataString(hostname)}&type=A");
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<DnsJsonResponse>(cancellationToken);
            return result is { Status: 0 } &&
                   result.Answer is not null &&
                   result.Answer.Any(answer => answer.Type is 1 or 5 && !string.IsNullOrWhiteSpace(answer.Data));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private sealed record DnsJsonResponse(
        [property: JsonPropertyName("Status")] int Status,
        [property: JsonPropertyName("Answer")] IReadOnlyList<DnsJsonAnswer>? Answer);

    private sealed record DnsJsonAnswer(
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("data")] string Data);
}
