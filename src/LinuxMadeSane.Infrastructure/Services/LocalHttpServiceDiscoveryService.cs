using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Cloudflare;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalHttpServiceDiscoveryService : ILocalHttpServiceDiscoveryService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1400);
    private static readonly Regex TitleRegex = new(
        "<title[^>]*>\\s*(?<title>.*?)\\s*</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public async Task<IReadOnlyList<LocalHttpServiceEndpoint>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var ports = GetLocalListeningPorts();
        if (ports.Count == 0)
        {
            return [];
        }

        var results = new ConcurrentBag<LocalHttpServiceEndpoint>();
        using var concurrency = new SemaphoreSlim(16);
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true
        };
        using var client = new HttpClient(handler)
        {
            Timeout = ProbeTimeout
        };

        var probes = ports
            .SelectMany(port => new[] { "https", "http" }.Select(scheme => ProbeAsync(client, concurrency, results, scheme, port, cancellationToken)))
            .ToArray();

        await Task.WhenAll(probes);

        return results
            .DistinctBy(endpoint => endpoint.Url, StringComparer.OrdinalIgnoreCase)
            .OrderBy(endpoint => endpoint.Port)
            .ThenBy(endpoint => endpoint.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToArray();
    }

    private static IReadOnlyList<int> GetLocalListeningPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Where(endpoint => IsLocalHttpCandidateAddress(endpoint.Address))
                .Select(endpoint => endpoint.Port)
                .Where(port => port > 0)
                .Distinct()
                .Order()
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static bool IsLocalHttpCandidateAddress(IPAddress address) =>
        IPAddress.IsLoopback(address) ||
        address.Equals(IPAddress.Any) ||
        address.Equals(IPAddress.IPv6Any);

    private static async Task ProbeAsync(
        HttpClient client,
        SemaphoreSlim concurrency,
        ConcurrentBag<LocalHttpServiceEndpoint> results,
        string scheme,
        int port,
        CancellationToken cancellationToken)
    {
        await concurrency.WaitAsync(cancellationToken);
        try
        {
            var endpoint = await TryProbeAsync(client, scheme, port, cancellationToken);
            if (endpoint is not null)
            {
                results.Add(endpoint);
            }
        }
        finally
        {
            concurrency.Release();
        }
    }

    private static async Task<LocalHttpServiceEndpoint?> TryProbeAsync(
        HttpClient client,
        string scheme,
        int port,
        CancellationToken cancellationToken)
    {
        var url = $"{scheme}://localhost:{port}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{url}/");
            request.Headers.UserAgent.ParseAdd("LinuxMadeSane-local-service-discovery");

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            var title = await TryReadTitleAsync(response, timeout.Token);
            return new LocalHttpServiceEndpoint(
                url,
                scheme,
                "localhost",
                port,
                (int)response.StatusCode,
                title,
                response.Headers.Server.ToString());
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryReadTitleAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null ||
            (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
             !mediaType.Contains("text", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[8192];
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead <= 0)
            {
                return null;
            }

            var sample = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var match = TitleRegex.Match(sample);
            if (!match.Success)
            {
                return null;
            }

            var title = WebUtility.HtmlDecode(match.Groups["title"].Value);
            return string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        catch
        {
            return null;
        }
    }
}
