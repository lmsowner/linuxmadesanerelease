// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed partial class WebResearchService(HttpClient httpClient) : IWebResearchService
{
    private const int MaxQueryLength = 500;
    private const int MaxSearchResults = 8;
    private const int MaxSearchResponseCharacters = 1_000_000;
    private const int MaxPageCharacters = 20_000;
    private const int MaxRedirects = 3;

    public async Task<SearchWebToolResponse> SearchAsync(
        SearchWebToolRequest request,
        CancellationToken cancellationToken = default)
    {
        var query = RequireQuery(request.Query);
        var domains = NormalizeDomains(request.Domains);
        var searchQuery = domains.Count == 0
            ? query
            : $"{query} {string.Join(' ', domains.Select(domain => $"site:{domain}"))}";
        var searchUri = new Uri($"https://www.bing.com/search?q={Uri.EscapeDataString(searchQuery)}");

        using var response = await httpClient.GetAsync(searchUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await ReadLimitedAsync(response, MaxSearchResponseCharacters, cancellationToken);
        var results = ParseSearchResults(html, domains)
            .Take(Math.Clamp(request.MaxResults, 1, MaxSearchResults))
            .ToArray();

        return new SearchWebToolResponse(query, results, DateTimeOffset.UtcNow);
    }

    public async Task<FetchWebPageToolResponse> FetchPageAsync(
        FetchWebPageToolRequest request,
        CancellationToken cancellationToken = default)
    {
        var uri = await ValidatePublicUriAsync(request.Url, cancellationToken);
        var maxCharacters = Math.Clamp(request.MaxCharacters, 1_000, MaxPageCharacters);

        for (var redirect = 0; ; redirect++)
        {
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                if (redirect >= MaxRedirects)
                {
                    throw new InvalidOperationException("The documentation page redirected too many times.");
                }

                uri = await ValidatePublicUriAsync(
                    location.IsAbsoluteUri ? location.ToString() : new Uri(uri, location).ToString(),
                    cancellationToken);
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (!IsSupportedTextContent(response.Content.Headers.ContentType))
            {
                throw new InvalidOperationException(
                    $"The page returned unsupported content type '{response.Content.Headers.ContentType?.MediaType ?? "unknown"}'. Fetch an HTML, text, Markdown, JSON, or XML documentation page.");
            }

            var rawContent = await ReadLimitedAsync(response, maxCharacters * 4, cancellationToken);
            var title = ExtractTitle(rawContent);
            var content = response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true
                ? ConvertHtmlToText(rawContent)
                : NormalizeText(rawContent);
            var truncated = content.Length > maxCharacters;
            if (truncated)
            {
                content = content[..maxCharacters].TrimEnd() + "\n[Page content truncated by LMS.]";
            }

            return new FetchWebPageToolResponse(uri.ToString(), title, content, truncated, DateTimeOffset.UtcNow);
        }
    }

    private static string RequireQuery(string? query)
    {
        var normalized = query?.Trim() ?? string.Empty;
        return normalized.Length is 0 or > MaxQueryLength
            ? throw new InvalidOperationException($"Web search queries must contain between 1 and {MaxQueryLength} characters.")
            : normalized;
    }

    private static IReadOnlyList<string> NormalizeDomains(IReadOnlyList<string>? domains) =>
        (domains ?? [])
            .Select(domain => domain.Trim().TrimStart('.').ToLowerInvariant())
            .Where(domain => domain.Length > 0 && domain.Length <= 253 && DomainPattern().IsMatch(domain))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();

    private async Task<Uri> ValidatePublicUriAsync(string? url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("Web research can retrieve only public HTTP or HTTPS URLs without embedded credentials.");
        }

        if (IsBlockedHost(uri.Host))
        {
            throw new InvalidOperationException("Web research cannot retrieve loopback, private, link-local, or local-domain URLs.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException($"The documentation host could not be resolved: {exception.Message}", exception);
        }

        if (addresses.Length == 0 || addresses.Any(IsBlockedAddress))
        {
            throw new InvalidOperationException("Web research cannot retrieve a host that resolves to a private or local address.");
        }

        return uri;
    }

    private static IReadOnlyList<AiWebSearchResult> ParseSearchResults(string html, IReadOnlyList<string> domains)
    {
        var titleMatches = BingSearchResultTitlePattern().Matches(html);
        var results = new List<AiWebSearchResult>();
        for (var index = 0; index < titleMatches.Count; index++)
        {
            var titleMatch = titleMatches[index];
            var nextStart = index + 1 < titleMatches.Count ? titleMatches[index + 1].Index : html.Length;
            var section = html[titleMatch.Index..nextStart];
            var url = ResolveSearchResultUrl(WebUtility.HtmlDecode(titleMatch.Groups["url"].Value));
            if (!Uri.TryCreate(url, UriKind.Absolute, out var resultUri) ||
                !resultUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !resultUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                IsBlockedHost(resultUri.Host) ||
                domains.Count > 0 && !domains.Any(domain => HostMatchesDomain(resultUri.Host, domain)))
            {
                continue;
            }

            var title = CleanHtml(titleMatch.Groups["title"].Value);
            var snippetMatch = BingSearchResultSnippetPattern().Match(section);
            var snippet = snippetMatch.Success ? CleanHtml(snippetMatch.Groups["snippet"].Value) : string.Empty;
            if (title.Length == 0)
            {
                continue;
            }

            results.Add(new AiWebSearchResult(title, resultUri.ToString(), snippet, resultUri.Host));
        }

        return results
            .GroupBy(result => result.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static string ResolveSearchResultUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var encodedTarget = query["uddg"];
        if (!string.IsNullOrWhiteSpace(encodedTarget))
        {
            return WebUtility.UrlDecode(encodedTarget);
        }

        var encodedBingTarget = query["u"];
        if (string.IsNullOrWhiteSpace(encodedBingTarget))
        {
            return url;
        }

        var decodedBingTarget = WebUtility.UrlDecode(encodedBingTarget);
        if (!decodedBingTarget.StartsWith("a1", StringComparison.OrdinalIgnoreCase))
        {
            return decodedBingTarget;
        }

        try
        {
            var base64 = decodedBingTarget[2..].Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return url;
        }
    }

    private static string ExtractTitle(string text)
    {
        var match = PageTitlePattern().Match(text);
        return match.Success ? CleanHtml(match.Groups["title"].Value) : string.Empty;
    }

    private static string ConvertHtmlToText(string html)
    {
        var withoutNonContent = NonContentPattern().Replace(html, " ");
        var withLineBreaks = LineBreakPattern().Replace(withoutNonContent, "\n");
        return NormalizeText(CleanHtml(withLineBreaks));
    }

    private static string CleanHtml(string value) =>
        NormalizeText(WebUtility.HtmlDecode(TagPattern().Replace(value, " ")));

    private static string NormalizeText(string value) =>
        Regex.Replace(value, @"[ \t\r\f\v]+", " ")
            .Replace(" \n", "\n", StringComparison.Ordinal)
            .Replace("\n ", "\n", StringComparison.Ordinal)
            .Trim();

    private static bool IsSupportedTextContent(MediaTypeHeaderValue? contentType) =>
        contentType?.MediaType is null ||
        contentType.MediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        contentType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
        contentType.MediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase) ||
        contentType.MediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadLimitedAsync(
        HttpResponseMessage response,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var buffer = new char[8192];
        var builder = new System.Text.StringBuilder(Math.Min(maxCharacters, 64 * 1024));
        while (builder.Length < maxCharacters)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxCharacters - builder.Length)), cancellationToken);
            if (read == 0)
            {
                break;
            }

            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }

    private static bool HostMatchesDomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlockedHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IsBlockedAddress(address);

    private static bool IsBlockedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   bytes[0] == 169 && bytes[1] == 254 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
               address.GetAddressBytes()[0] == 0xfc || address.GetAddressBytes()[0] == 0xfd;
    }

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9.-]{0,251}[a-z0-9])?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DomainPattern();

    [GeneratedRegex("""<li[^>]*class=["'][^"']*\bb_algo\b[^"']*["'][^>]*>.*?<h2[^>]*>\s*<a[^>]*href=["'](?<url>[^"']+)["'][^>]*>(?<title>.*?)</a>\s*</h2>""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BingSearchResultTitlePattern();

    [GeneratedRegex("""<p[^>]*class=["'][^"']*\bb_lineclamp[^"']*["'][^>]*>(?<snippet>.*?)</p>""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BingSearchResultSnippetPattern();

    [GeneratedRegex(@"<title[^>]*>(?<title>.*?)</title>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PageTitlePattern();

    [GeneratedRegex(@"<script\b[^>]*>.*?</script\s*>|<style\b[^>]*>.*?</style\s*>|<noscript\b[^>]*>.*?</noscript\s*>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NonContentPattern();

    [GeneratedRegex(@"</?(?:p|div|section|article|main|header|footer|li|br|h[1-6]|pre|blockquote|tr|td|th)[^>]*>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakPattern();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex TagPattern();
}
