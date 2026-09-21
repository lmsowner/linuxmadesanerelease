// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Text.RegularExpressions;

namespace LinuxMadeSane.Infrastructure.Services;

internal static partial class WebtorStremioIntegrationClient
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(45);

    public static async Task<string> EnsureAddonPathAsync(Uri localBaseUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localBaseUri);
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CookieContainer = new CookieContainer()
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = localBaseUri,
            Timeout = TimeSpan.FromSeconds(10)
        };

        var deadline = DateTimeOffset.UtcNow + ReadyTimeout;
        string profile = string.Empty;
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync("profile", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    profile = await response.Content.ReadAsStringAsync(cancellationToken);
                    break;
                }
                lastError = new HttpRequestException($"Webtor profile returned HTTP {(int)response.StatusCode}.");
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lastError = exception;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(profile))
        {
            throw new InvalidOperationException("Webtor did not become ready for Stremio addon setup.", lastError);
        }

        if (TryExtractAddonPath(profile, out var existingPath))
        {
            return existingPath;
        }

        var csrf = ExtractCsrfToken(profile);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(csrf), "_csrf");
        using var generate = await client.PostAsync("stremio/url/generate", content, cancellationToken);
        if (generate.StatusCode is not HttpStatusCode.Found and not HttpStatusCode.SeeOther && !generate.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Webtor rejected Stremio addon setup with HTTP {(int)generate.StatusCode}.");
        }

        using var refreshed = await client.GetAsync("profile", cancellationToken);
        refreshed.EnsureSuccessStatusCode();
        profile = await refreshed.Content.ReadAsStringAsync(cancellationToken);
        if (!TryExtractAddonPath(profile, out var addonPath))
        {
            throw new InvalidOperationException("Webtor created the Stremio addon but did not expose its install URL.");
        }

        return addonPath;
    }

    internal static bool TryExtractAddonPath(string html, out string path)
    {
        var match = AddonPathRegex().Match(WebUtility.HtmlDecode(html ?? string.Empty));
        path = match.Success ? match.Groups[1].Value : string.Empty;
        return match.Success;
    }

    internal static string ExtractCsrfToken(string html)
    {
        var match = CsrfRegex().Match(html ?? string.Empty);
        if (!match.Success)
        {
            throw new InvalidOperationException("Webtor did not provide the CSRF token required to create its Stremio addon URL.");
        }
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [GeneratedRegex("(/s/[a-zA-Z0-9_-]+/manifest\\.json)", RegexOptions.CultureInvariant)]
    private static partial Regex AddonPathRegex();

    [GeneratedRegex("name=[\"']_csrf[\"'][^>]*value=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CsrfRegex();
}
