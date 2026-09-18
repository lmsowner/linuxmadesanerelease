// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net.Http.Json;
using System.Text.Json.Serialization;
using LinuxMadeSane.Application.Interfaces;

namespace LinuxMadeSane.Infrastructure.Services.Cloudflare;

public sealed class PublicDnsPropagationService : IPublicDnsPropagationService
{
    private static readonly string[] ResolverEndpoints =
    [
        "https://cloudflare-dns.com/dns-query",
        "https://dns.google/resolve"
    ];
    private readonly HttpClient httpClient;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan initialDelay;
    private readonly TimeSpan retryDelay;
    private readonly int requiredConsecutiveAnswers;

    public PublicDnsPropagationService(HttpClient httpClient, TimeProvider timeProvider)
        : this(httpClient, timeProvider, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(1), 2)
    {
    }

    internal PublicDnsPropagationService(
        HttpClient httpClient,
        TimeProvider timeProvider,
        TimeSpan initialDelay,
        TimeSpan retryDelay,
        int requiredConsecutiveAnswers)
    {
        this.httpClient = httpClient;
        this.timeProvider = timeProvider;
        this.initialDelay = initialDelay;
        this.retryDelay = retryDelay;
        this.requiredConsecutiveAnswers = requiredConsecutiveAnswers;
    }

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
        var consecutiveAnswers = 0;
        await Task.Delay(initialDelay, timeProvider, cancellationToken);
        while (timeProvider.GetUtcNow() < deadline)
        {
            var resolved = true;
            foreach (var endpoint in ResolverEndpoints)
            {
                if (!await ResolvesAsync(endpoint, normalizedHostname, cancellationToken))
                {
                    resolved = false;
                    break;
                }
            }

            consecutiveAnswers = resolved ? consecutiveAnswers + 1 : 0;
            if (consecutiveAnswers >= requiredConsecutiveAnswers)
            {
                return true;
            }

            var remaining = deadline - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(
                remaining < retryDelay ? remaining : retryDelay,
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
