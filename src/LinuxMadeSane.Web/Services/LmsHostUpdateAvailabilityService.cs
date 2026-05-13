using System.Text.Json;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Versioning;
using Microsoft.Extensions.Options;

namespace LinuxMadeSane.Web.Services;

public enum LmsHostUpdateAvailabilityState
{
    Current,
    UpdateAvailable,
    Unknown
}

public sealed record LmsHostUpdateAvailability(
    Guid HostId,
    LmsHostUpdateAvailabilityState State,
    string InstalledVersion,
    string LatestVersion,
    string Detail)
{
    public bool IsUpdateAvailable => State == LmsHostUpdateAvailabilityState.UpdateAvailable;
    public bool IsDirectWebAvailable => State != LmsHostUpdateAvailabilityState.Unknown &&
                                        !string.IsNullOrWhiteSpace(InstalledVersion) &&
                                        !InstalledVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
}

public sealed record LmsHostWebAccessStatus(
    Guid HostId,
    bool IsDirectWebAvailable,
    string InstalledVersion,
    string Detail);

public sealed class LmsHostUpdateAvailabilityService(
    HttpClient httpClient,
    IOptionsMonitor<ApplicationUpdateOptions> optionsMonitor,
    ILogger<LmsHostUpdateAvailabilityService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(4);

    public async Task<IReadOnlyDictionary<Guid, LmsHostUpdateAvailability>> CheckHostsAsync(
        IReadOnlyCollection<ManagedHost> hosts,
        CancellationToken cancellationToken = default)
    {
        if (hosts.Count == 0)
        {
            return new Dictionary<Guid, LmsHostUpdateAvailability>();
        }

        var latestVersion = await GetLatestVersionAsync(cancellationToken);
        var work = hosts
            .Where(ManagedHostCapabilities.IsLmsHost)
            .Select(host => CheckHostAsync(host, latestVersion, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(work);
        return results.ToDictionary(result => result.HostId);
    }

    public async Task<IReadOnlyDictionary<Guid, LmsHostWebAccessStatus>> CheckDirectWebAccessAsync(
        IReadOnlyCollection<ManagedHost> hosts,
        CancellationToken cancellationToken = default)
    {
        if (hosts.Count == 0)
        {
            return new Dictionary<Guid, LmsHostWebAccessStatus>();
        }

        var work = hosts
            .Where(ManagedHostCapabilities.IsLmsHost)
            .Select(host => CheckDirectWebAccessAsync(host, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(work);
        return results.ToDictionary(result => result.HostId);
    }

    public async Task<LmsHostWebAccessStatus> CheckDirectWebAccessAsync(
        ManagedHost host,
        CancellationToken cancellationToken = default)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            return new LmsHostWebAccessStatus(
                host.Id,
                true,
                LinuxMadeSaneBuildVersion.GetCurrent(typeof(Program).Assembly),
                "Local LMS web UI is available.");
        }

        var installedVersion = await ProbeRemoteInstalledVersionAsync(host, cancellationToken);
        return string.IsNullOrWhiteSpace(installedVersion)
            ? new LmsHostWebAccessStatus(
                host.Id,
                false,
                "Unknown",
                BuildDirectWebUnavailableDetail())
            : new LmsHostWebAccessStatus(
                host.Id,
                true,
                installedVersion,
                $"Remote LMS web UI answered on port 5080. Installed version: {installedVersion}.");
    }

    private async Task<LmsHostUpdateAvailability> CheckHostAsync(
        ManagedHost host,
        string latestVersion,
        CancellationToken cancellationToken)
    {
        var installedVersion = AiLocalMachine.IsLocalMachine(host.Id)
            ? LinuxMadeSaneBuildVersion.GetCurrent(typeof(Program).Assembly)
            : await ProbeRemoteInstalledVersionAsync(host, cancellationToken);

        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            return new LmsHostUpdateAvailability(
                host.Id,
                LmsHostUpdateAvailabilityState.Unknown,
                "Unknown",
                latestVersion,
                BuildDirectWebUnavailableDetail());
        }

        var state = ApplicationUpdateVersionComparer.IsNewer(latestVersion, installedVersion)
            ? LmsHostUpdateAvailabilityState.UpdateAvailable
            : LmsHostUpdateAvailabilityState.Current;
        var detail = state == LmsHostUpdateAvailabilityState.UpdateAvailable
            ? $"{installedVersion} -> {latestVersion}"
            : installedVersion;

        return new LmsHostUpdateAvailability(
            host.Id,
            state,
            installedVersion,
            latestVersion,
            detail);
    }

    private async Task<string> GetLatestVersionAsync(CancellationToken cancellationToken)
    {
        var manifestUrl = NormalizeAbsoluteUrl(
            optionsMonitor.CurrentValue.ManifestUrl,
            "https://www.linuxmadesane.com/api/downloads/manifest");

        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCts.CancelAfter(ManifestTimeout);

        using var response = await httpClient.GetAsync(manifestUrl, requestCts.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(requestCts.Token);
        var manifest = await JsonSerializer.DeserializeAsync<ReleaseManifestDto>(stream, JsonOptions, requestCts.Token);
        if (manifest is null)
        {
            throw new InvalidOperationException("The release manifest was empty.");
        }

        return FirstNonBlank(
            manifest.LatestVersion,
            manifest.LatestCommunityVersion,
            manifest.LatestProVersion) ?? throw new InvalidOperationException("The release manifest did not include a latest version.");
    }

    private async Task<string> ProbeRemoteInstalledVersionAsync(
        ManagedHost host,
        CancellationToken cancellationToken)
    {
        foreach (var uri in BuildHealthUris(host))
        {
            try
            {
                using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestCts.CancelAfter(HealthProbeTimeout);
                using var response = await httpClient.GetAsync(uri, requestCts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync(requestCts.Token);
                var version = TryReadHealthVersion(content);
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "LMS host version probe failed for {Host} at {Uri}.", host.Name, uri);
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<Uri> BuildHealthUris(ManagedHost host)
    {
        var endpoint = host.Hostname.Trim();
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            endpoint = uri.Host;
        }

        return
        [
            new UriBuilder(Uri.UriSchemeHttp, endpoint, 5080, "healthz").Uri,
            new UriBuilder(Uri.UriSchemeHttps, endpoint, 5080, "healthz").Uri
        ];
    }

    private static string TryReadHealthVersion(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var product = ReadJsonString(root, "product");
            var name = ReadJsonString(root, "name");
            var status = ReadJsonString(root, "status");
            var version = ReadJsonString(root, "version");
            var hasProductMarker =
                string.Equals(product, "linux-made-sane", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Linux Made Sane", StringComparison.OrdinalIgnoreCase);

            return hasProductMarker &&
                   string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) &&
                   HasLinuxMadeSaneVersionShape(version)
                ? version!.Trim()
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string? ReadJsonString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool HasLinuxMadeSaneVersionShape(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var normalized = version.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts is { Length: 4 or 5 } && parts.All(static part => part.All(char.IsDigit));
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string BuildDirectWebUnavailableDetail() =>
        "LMS is installed and SSH management may still work, but the remote LMS web endpoint on port 5080 is not directly reachable. Use Portal/Pro relay or Edge Gateway for browser access.";

    private static string NormalizeAbsoluteUrl(string? value, string fallback)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri))
        {
            return uri.ToString();
        }

        return fallback;
    }

    private sealed record ReleaseManifestDto(
        string LatestVersion,
        string LatestCommunityVersion,
        string LatestProVersion);
}
