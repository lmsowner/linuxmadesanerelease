// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Net;
using System.Text.Json;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Services;

public sealed record LmsWebUiStatus(Uri? Origin, string Version, string? Channel, bool Available, bool AccessDenied)
{
    public Uri? SignInPage { get; init; }
    public static readonly LmsWebUiStatus Unreachable = new(null, "", null, false, false);
}

public sealed class LmsWebUiProbe(HttpClient client)
{
    public async Task<LmsWebUiStatus> CheckAsync(ManagedHost host, CancellationToken token)
    {
        var endpoint = host.Hostname.Trim();
        var origins = new List<Uri>();
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var configured) &&
            configured.Scheme is "https" or "http" && string.IsNullOrEmpty(configured.UserInfo))
        {
            origins.Add(new Uri(configured, "/"));
            endpoint = configured.Host;
        }
        foreach (var (scheme, port) in new[] { ("https", 5443), ("https", 443), ("https", 5080), ("http", 5080) })
            origins.Add(new UriBuilder(scheme, endpoint, port).Uri);

        var results = await Task.WhenAll(origins.Distinct().Select(origin => CheckOriginAsync(origin, token)));
        return results.FirstOrDefault(result => result.Available)
            ?? results.FirstOrDefault(result => result.AccessDenied)
            ?? results.FirstOrDefault(result => result.Origin is not null)
            ?? LmsWebUiStatus.Unreachable;
    }

    private async Task<LmsWebUiStatus> CheckOriginAsync(Uri origin, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var status = LmsWebUiStatus.Unreachable;
        try
        {
            using var health = await client.GetAsync(new Uri(origin, "/healthz"), timeout.Token);
            if (!health.IsSuccessStatusCode) return status;
            var body = await health.Content.ReadAsStringAsync(timeout.Token);
            var version = LmsHostUpdateAvailabilityService.TryReadHealthVersion(body);
            if (version.Length == 0) return status;
            using var json = JsonDocument.Parse(body);
            var channel = json.RootElement.TryGetProperty("releaseChannel", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            status = new(origin, version, channel, false, false);

            var page = origin;
            var checkedLoginDuringSetup = false;
            for (var hop = 0; hop < 5; hop++)
            {
                using var response = await client.GetAsync(page, timeout.Token);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                    return status with { AccessDenied = true };
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    var next = new Uri(page, location);
                    if (next.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority)) return status;
                    if (next.AbsolutePath.Equals("/access-denied", StringComparison.OrdinalIgnoreCase))
                        return status with { AccessDenied = true };
                    page = next;
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "text/html") return status;
                var html = await response.Content.ReadAsStringAsync(timeout.Token);
                // A still-valid setup code can make the setup page reachable even after
                // enrollment, while the actual anonymous login route is still blocked.
                if (!checkedLoginDuringSetup && page.AbsolutePath.Equals("/setup", StringComparison.OrdinalIgnoreCase))
                {
                    checkedLoginDuringSetup = true;
                    page = new Uri(origin, "/login");
                    continue;
                }
                if (html.Contains("<h1>Access denied</h1>", StringComparison.OrdinalIgnoreCase))
                    return status with { AccessDenied = true };
                if ((page.AbsolutePath.Equals("/login", StringComparison.OrdinalIgnoreCase) ||
                    page.AbsolutePath.Equals("/LMSMFALogin", StringComparison.OrdinalIgnoreCase)) &&
                    !html.Contains("auth-login-form", StringComparison.OrdinalIgnoreCase) &&
                    !html.Contains("name=\"otpCode\"", StringComparison.OrdinalIgnoreCase)) return status;
                return status with { SignInPage = page, Available = html.Contains("_framework/blazor", StringComparison.OrdinalIgnoreCase) &&
                    (html.Contains("LinuxMadeSane.Web", StringComparison.OrdinalIgnoreCase) || html.Contains("lms-logo-", StringComparison.OrdinalIgnoreCase)) };
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or UriFormatException or IOException) { }
        return status;
    }
}
