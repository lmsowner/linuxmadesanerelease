// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
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
    public string? ReleaseChannel { get; init; }
    public bool IsUpdateAvailable => State == LmsHostUpdateAvailabilityState.UpdateAvailable;
    public bool? DirectWebReachable { get; init; }
    public bool IsDirectWebAvailable => DirectWebReachable ?? (!string.IsNullOrWhiteSpace(InstalledVersion) &&
                                        !InstalledVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase));
}

public sealed record LmsHostWebAccessStatus(
    Guid HostId,
    bool IsDirectWebAvailable,
    string InstalledVersion,
    string Detail);

public sealed record LmsLatestVersionCheck(string Version, string Failure)
{
    public bool HasVersion => !string.IsNullOrWhiteSpace(Version);
}

public sealed class LmsHostUpdateAvailabilityService(
    HttpClient httpClient,
    IOptionsMonitor<ApplicationUpdateOptions> optionsMonitor,
    ILogger<LmsHostUpdateAvailabilityService> logger,
    ApplicationUpdateService? localUpdates = null,
    IServiceScopeFactory? scopes = null)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> hostChannels = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> hostWebAccess = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> directBaseUrls = new();
    public string? GetDirectBaseUrl(Guid hostId) => directBaseUrls.GetValueOrDefault(hostId);
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

        var latestVersionCheck = await CheckLatestVersionAsync(cancellationToken);

        var work = hosts
            .Where(ManagedHostCapabilities.IsLmsHost)
            .Select(host => CheckHostAsync(host, latestVersionCheck, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(work);
        return results.ToDictionary(result => result.HostId);
    }

    public async Task<LmsLatestVersionCheck> CheckLatestVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return new LmsLatestVersionCheck(await GetLatestVersionAsync(cancellationToken), string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = SimplifyFailureMessage(ex);
            logger.LogDebug(ex, "LMS release manifest check failed.");
            return new LmsLatestVersionCheck(string.Empty, failure);
        }
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
                $"Remote LMS web UI is reachable. Installed version: {installedVersion}.");
    }

    public async Task<LmsHostUpdateAvailability> CheckHostAsync(
        ManagedHost host,
        LmsLatestVersionCheck latestVersionCheck,
        CancellationToken cancellationToken)
    {
        var installedVersion = AiLocalMachine.IsLocalMachine(host.Id)
            ? LinuxMadeSaneBuildVersion.GetCurrent(typeof(Program).Assembly)
            : await ProbeRemoteInstalledVersionAsync(host, cancellationToken, allowSshFallback: true);

        var channel = AiLocalMachine.IsLocalMachine(host.Id)
            ? localUpdates?.GetStatus().Channel ?? "stable"
            : hostChannels.GetValueOrDefault(host.Id, "stable");
        if (channel == "development")
        {
            try { latestVersionCheck = new LmsLatestVersionCheck(await GetLatestVersionAsync(cancellationToken, channel), ""); }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { latestVersionCheck = new LmsLatestVersionCheck("", SimplifyFailureMessage(ex)); }
        }
        return BuildAvailability(
            host.Id,
            installedVersion,
            latestVersionCheck.Version,
            latestVersionCheck.Failure) with { ReleaseChannel = AiLocalMachine.IsLocalMachine(host.Id) ? channel : hostChannels.GetValueOrDefault(host.Id), DirectWebReachable = AiLocalMachine.IsLocalMachine(host.Id) || hostWebAccess.GetValueOrDefault(host.Id) };
    }

    public static LmsHostUpdateAvailability BuildAvailability(
        Guid hostId,
        string installedVersion,
        string latestVersion,
        string latestVersionFailure)
    {
        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            return new LmsHostUpdateAvailability(
                hostId,
                LmsHostUpdateAvailabilityState.Unknown,
                "Unknown",
                FirstNonBlank(latestVersion, "Unknown") ?? "Unknown",
                BuildDirectWebUnavailableDetail());
        }

        if (string.IsNullOrWhiteSpace(latestVersion))
        {
            return new LmsHostUpdateAvailability(
                hostId,
                LmsHostUpdateAvailabilityState.Unknown,
                installedVersion,
                "Unknown",
                BuildLatestVersionUnavailableDetail(installedVersion, latestVersionFailure));
        }

        var state = ApplicationUpdateVersionComparer.IsNewer(latestVersion, installedVersion)
            ? LmsHostUpdateAvailabilityState.UpdateAvailable
            : LmsHostUpdateAvailabilityState.Current;
        var detail = state == LmsHostUpdateAvailabilityState.UpdateAvailable
            ? $"{installedVersion} -> {latestVersion}"
            : installedVersion;

        return new LmsHostUpdateAvailability(
            hostId,
            state,
            installedVersion,
            latestVersion,
            detail);
    }

    private async Task<string> GetLatestVersionAsync(CancellationToken cancellationToken, string channel = "stable")
    {
        var manifestUrl = NormalizeAbsoluteUrl(
            optionsMonitor.CurrentValue.ManifestUrl,
            "https://www.linuxmadesane.com/api/downloads/manifest");

        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCts.CancelAfter(ManifestTimeout);

        using var response = await httpClient.GetAsync(ApplicationReleaseChannel.Url(manifestUrl, channel), requestCts.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(requestCts.Token);
        var manifest = await JsonSerializer.DeserializeAsync<ReleaseManifestDto>(stream, JsonOptions, requestCts.Token);
        if (manifest is null)
        {
            throw new InvalidOperationException("The release manifest was empty.");
        }

        return (manifest.Channel is not null ? manifest.LatestCommunityVersion : FirstNonBlank(
            manifest.LatestCommunityVersion,
            manifest.LatestVersion)) ?? throw new InvalidOperationException("The release manifest did not include a latest version.");
    }

    private async Task<string> ProbeRemoteInstalledVersionAsync(
        ManagedHost host,
        CancellationToken cancellationToken, bool allowSshFallback = false)
    {
        var webVersion = string.Empty;
        hostWebAccess[host.Id] = false;
        hostChannels.TryRemove(host.Id, out _);
        directBaseUrls.TryRemove(host.Id, out _);
        // Probe together, then choose in HTTPS-first order. An unreachable endpoint must not multiply delays.
        var probes = BuildHealthUris(host).Select(async uri =>
        {
            try
            {
                using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestCts.CancelAfter(HealthProbeTimeout);
                using var response = await httpClient.GetAsync(uri, requestCts.Token);
                if (!response.IsSuccessStatusCode) return ((Uri Uri, string Version, string? Channel)?)null;
                var content = await response.Content.ReadAsStringAsync(requestCts.Token);
                var version = TryReadHealthVersion(content);
                if (string.IsNullOrWhiteSpace(version)) return null;
                using var health = JsonDocument.Parse(content);
                return (uri, version, ReadJsonString(health.RootElement, "releaseChannel"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogDebug(ex, "LMS host version probe failed for {Host} at {Uri}.", host.Name, uri); return null; }
        });
        var reachable = (await Task.WhenAll(probes)).FirstOrDefault(result => result is not null);
        if (reachable is { } access)
        {
            hostWebAccess[host.Id] = true;
            directBaseUrls[host.Id] = new Uri(access.Uri, "/").ToString();
            webVersion = access.Version;
            if (access.Channel is "stable" or "development") { hostChannels[host.Id] = access.Channel; return webVersion; }
        }

        if (allowSshFallback && scopes is not null)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var commands = scope.ServiceProvider.GetRequiredService<ICommandExecutionService>();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var result = await commands.ExecuteAsync(host, SavedReleaseMetadataCommand, cancellationToken: timeout.Token);
                if (result.IsSuccess)
                {
                    using var metadata = JsonDocument.Parse(result.StandardOutput.Trim());
                    var channel = ReadJsonString(metadata.RootElement, "releaseChannel");
                    if (channel is "stable" or "development") hostChannels[host.Id] = channel;
                    var version = ReadJsonString(metadata.RootElement, "version");
                    if (HasLinuxMadeSaneVersionShape(version)) return version!;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogDebug(ex, "Saved LMS release metadata probe failed for {Host}.", host.Name); }
        }
        return webVersion;
    }

    private const string SavedReleaseMetadataScript = """
        import json,pathlib
        root=pathlib.Path('/var/lib/linuxmadesane/ce'); channel=None
        p=root/'update-channel.json'
        if p.exists():
         try:
          value=json.loads(p.read_text()).get('channel')
          if value in ('stable','development'): channel=value
         except (OSError,ValueError): pass
        if channel is None:
         p=pathlib.Path('/etc/linuxmadesane/ce/service.env')
         if p.exists():
          for line in p.read_text().splitlines():
           if line.startswith('ApplicationUpdates__Channel='):
            value=line.split('=',1)[1].strip().strip('"').strip("'")
            if value in ('stable','development'): channel=value
        version=None
        for name in ('/opt/linuxmadesane/ce/current/version.txt','/opt/linuxmadesane/ce/current/app/version.txt'):
         p=pathlib.Path(name)
         if p.exists():
          version=p.read_text().strip(); break
        print(json.dumps({'releaseChannel':channel,'version':version}))
        """;
    private static readonly string SavedReleaseMetadataCommand = BuildSavedReleaseMetadataCommand();
    private static string BuildSavedReleaseMetadataCommand()
    {
        var program = "python3 -c "+"\'" + SavedReleaseMetadataScript.Replace("\'", "\'\\\'\'") + "\'";
        return "if [ \"$(id -u)\" = 0 ]; then " + program + "; else sudo -n " + program + " 2>/dev/null || " + program + "; fi";
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
            new UriBuilder(Uri.UriSchemeHttps, endpoint, 443, "healthz").Uri,
            new UriBuilder(Uri.UriSchemeHttps, endpoint, 5080, "healthz").Uri,
            new UriBuilder(Uri.UriSchemeHttp, endpoint, 5080, "healthz").Uri
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
        "LMS is installed and SSH management may still work, but direct HTTPS/HTTP access to the remote LMS is unavailable. Use Portal/Pro relay or Edge Gateway for browser access.";

    private static string BuildLatestVersionUnavailableDetail(string installedVersion, string failure) =>
        string.IsNullOrWhiteSpace(failure)
            ? $"Installed version: {installedVersion}. Latest version check unavailable."
            : $"Installed version: {installedVersion}. Latest version check unavailable: {failure}";

    private static string SimplifyFailureMessage(Exception exception)
    {
        var message = exception.GetBaseException().Message.Trim();
        return string.IsNullOrWhiteSpace(message)
            ? exception.GetType().Name
            : message;
    }

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
        string LatestProVersion)
    {
        public string? Channel { get; init; }
    }
}
