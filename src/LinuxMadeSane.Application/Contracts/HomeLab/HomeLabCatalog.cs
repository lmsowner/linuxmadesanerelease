// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.HomeLab;

namespace LinuxMadeSane.Application.Contracts.HomeLab;

public static class HomeLabCatalog
{
    public static IReadOnlyList<HomeLabAppManifest> Apps { get; } =
    [
        new(
            "vpn-gateway",
            "VPN Gateway",
            "Reusable Gluetun gateway for apps that need VPN-routed internet access.",
            HomeLabAppCategory.Infrastructure,
            "vpn",
            "https://github.com/qdm12/gluetun",
            "https://github.com/qdm12/gluetun-wiki",
            "qmcgaw/gluetun",
            "latest",
            "1",
            [new("http-proxy", 8888, Primary: true)],
            [new("config", "/gluetun", HomeLabStorageKind.Configuration)],
            new Dictionary<string, string>(),
            [],
            new HomeLabHealthCheckManifest(DockerCommand: "wget -qO- http://127.0.0.1:9999/ >/dev/null || exit 1"),
            [
                new("provider", "Provider", "select", true, Options: ["ProtonVPN", "NordVPN", "Mullvad", "Surfshark", "Private Internet Access", "Custom WireGuard", "Custom OpenVPN"]),
                new("protocol", "Protocol", "select", true, Options: ["WireGuard", "OpenVPN"]),
                new("credentials", "Credentials or configuration", "secret", true, Secret: true)
            ],
            SupportsVpnGateway: false,
            IsInstallable: false),
        new(
            "qbittorrent",
            "qBittorrent",
            "Open-source BitTorrent client with a web interface.",
            HomeLabAppCategory.Download,
            "download",
            "https://www.qbittorrent.org/",
            "https://docs.linuxserver.io/images/docker-qbittorrent/",
            "lscr.io/linuxserver/qbittorrent",
            "latest",
            "1",
            [new("web", 8080, Primary: true)],
            [
                new("config", "/config", HomeLabStorageKind.Configuration),
                new("downloads", "/downloads", HomeLabStorageKind.UserData, "downloads")
            ],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PUID"] = "1000",
                ["PGID"] = "1000",
                ["TZ"] = "UTC"
            },
            [],
            new HomeLabHealthCheckManifest(HttpPath: "/", Port: 8080),
            [],
            SupportsVpnGateway: true),
        new(
            "jellyfin",
            "Jellyfin",
            "Free media server for movies, television, music, and live media.",
            HomeLabAppCategory.Media,
            "film",
            "https://jellyfin.org/",
            "https://jellyfin.org/docs/general/installation/container/",
            "jellyfin/jellyfin",
            "latest",
            "1",
            [new("web", 8096, Primary: true)],
            [
                new("config", "/config", HomeLabStorageKind.Configuration),
                new("cache", "/cache", HomeLabStorageKind.Cache),
                new("movies", "/movies", HomeLabStorageKind.UserData, "movies", ReadOnly: true),
                new("tv", "/tv", HomeLabStorageKind.UserData, "tv", ReadOnly: true)
            ],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["JELLYFIN_PublishedServerUrl"] = ""
            },
            [],
            new HomeLabHealthCheckManifest(HttpPath: "/health", Port: 8096),
            []),
        new(
            "stremio-server",
            "Stremio Server",
            "Stremio streaming server for a self-hosted home media setup.",
            HomeLabAppCategory.Media,
            "play",
            "https://www.stremio.com/",
            "https://www.stremio.com/",
            "stremio/server",
            "latest",
            "1",
            [new("web", 11470, Primary: true)],
            [new("config", "/root/.stremio-server", HomeLabStorageKind.Configuration)],
            new Dictionary<string, string>(), [], null, [], SupportsVpnGateway: true, IsInstallable: false),
        new(
            "immich",
            "Immich",
            "Self-hosted photo and video management.",
            HomeLabAppCategory.Photos,
            "photo",
            "https://immich.app/",
            "https://immich.app/docs/overview/introduction",
            "ghcr.io/immich-app/immich-server",
            "release",
            "1",
            [new("web", 2283, Primary: true)],
            [new("library", "/usr/src/app/upload", HomeLabStorageKind.UserData, "photos")],
            new Dictionary<string, string>(), ["immich-database", "immich-redis"], null, [], IsInstallable: false),
        new(
            "webtor",
            "Webtor",
            "Browser-based torrent streaming service.",
            HomeLabAppCategory.Utilities,
            "play",
            "https://webtor.io/",
            "https://webtor.io/",
            "webtor/webtor",
            "latest",
            "1",
            [new("web", 8080, Primary: true)],
            [new("config", "/config", HomeLabStorageKind.Configuration)],
            new Dictionary<string, string>(), [], null, [], SupportsVpnGateway: true, IsInstallable: false)
        ,
        new(
            "prowlarr",
            "Prowlarr",
            "Indexer manager for the media automation stack.",
            HomeLabAppCategory.Automation,
            "search",
            "https://prowlarr.com/",
            "https://wiki.servarr.com/prowlarr",
            "lscr.io/linuxserver/prowlarr",
            "latest",
            "1",
            [new("web", 9696, Primary: true)],
            [new("config", "/config", HomeLabStorageKind.Configuration)],
            new Dictionary<string, string>(), [], new HomeLabHealthCheckManifest(HttpPath: "/", Port: 9696), [], IsInstallable: false),
        new(
            "sonarr",
            "Sonarr",
            "Series management for an automated media library.",
            HomeLabAppCategory.Automation,
            "film",
            "https://sonarr.tv/",
            "https://wiki.servarr.com/sonarr",
            "lscr.io/linuxserver/sonarr",
            "latest",
            "1",
            [new("web", 8989, Primary: true)],
            [
                new("config", "/config", HomeLabStorageKind.Configuration),
                new("downloads", "/downloads", HomeLabStorageKind.UserData, "downloads"),
                new("tv", "/tv", HomeLabStorageKind.UserData, "tv")
            ],
            new Dictionary<string, string>(), [], new HomeLabHealthCheckManifest(HttpPath: "/", Port: 8989), [], IsInstallable: false),
        new(
            "radarr",
            "Radarr",
            "Movie management for an automated media library.",
            HomeLabAppCategory.Automation,
            "film",
            "https://radarr.video/",
            "https://wiki.servarr.com/radarr",
            "lscr.io/linuxserver/radarr",
            "latest",
            "1",
            [new("web", 7878, Primary: true)],
            [
                new("config", "/config", HomeLabStorageKind.Configuration),
                new("downloads", "/downloads", HomeLabStorageKind.UserData, "downloads"),
                new("movies", "/movies", HomeLabStorageKind.UserData, "movies")
            ],
            new Dictionary<string, string>(), [], new HomeLabHealthCheckManifest(HttpPath: "/", Port: 7878), [], IsInstallable: false),
        new(
            "seerr",
            "Seerr",
            "Request management UI for Sonarr and Radarr.",
            HomeLabAppCategory.Automation,
            "list",
            "https://seerr.dev/",
            "https://docs.seerr.dev/",
            "fallenbagel/jellyseerr",
            "latest",
            "1",
            [new("web", 5055, Primary: true)],
            [new("config", "/app/config", HomeLabStorageKind.Configuration)],
            new Dictionary<string, string>(), [], new HomeLabHealthCheckManifest(HttpPath: "/", Port: 5055), [], IsInstallable: false)
    ];

    public static IReadOnlyList<HomeLabRecipeManifest> Recipes { get; } =
    [
        new(
            "private-qbittorrent",
            "Private qBittorrent",
            "qBittorrent routed through the reusable VPN Gateway.",
            ["vpn-gateway", "qbittorrent"],
            ["downloads"],
            [new("qbittorrent", RouteVia: "vpn-gateway")],
            RequiresVpnGateway: true,
            IsInstallable: false),
        new(
            "private-stremio",
            "Private Stremio",
            "Stremio Server routed through the reusable VPN Gateway.",
            ["vpn-gateway", "stremio-server"],
            [],
            [new("stremio-server", RouteVia: "vpn-gateway")],
            RequiresVpnGateway: true,
            IsInstallable: false),
        new(
            "private-webtor",
            "Private Webtor",
            "Webtor routed through the reusable VPN Gateway.",
            ["vpn-gateway", "webtor"],
            [],
            [new("webtor", RouteVia: "vpn-gateway")],
            RequiresVpnGateway: true,
            IsInstallable: false),
        new(
            "media-automation",
            "Media Automation Stack",
            "qBittorrent, Prowlarr, Sonarr, Radarr, and Seerr on one private network.",
            ["qbittorrent", "prowlarr", "sonarr", "radarr", "seerr"],
            ["downloads", "movies", "tv"],
            [
                new("sonarr", DownloadClient: "qbittorrent", IndexerManager: "prowlarr"),
                new("radarr", DownloadClient: "qbittorrent", IndexerManager: "prowlarr"),
                new("seerr", Sonarr: "sonarr", Radarr: "radarr")
            ],
            IsInstallable: false),
        new(
            "jellyfin-media",
            "Jellyfin Media Stack",
            "Jellyfin and qBittorrent sharing LMS-managed media storage.",
            ["jellyfin", "qbittorrent"],
            ["downloads", "movies", "tv"],
            [new("jellyfin", DownloadClient: "qbittorrent")]),
        new(
            "private-jellyfin-media",
            "Private Jellyfin Media Stack",
            "Jellyfin Media Stack with qBittorrent routed through Gluetun.",
            ["vpn-gateway", "qbittorrent", "jellyfin"],
            ["downloads", "movies", "tv"],
            [
                new("qbittorrent", RouteVia: "vpn-gateway"),
                new("jellyfin", DownloadClient: "qbittorrent")
            ],
            RequiresVpnGateway: true,
            IsInstallable: false)
    ];

    public static HomeLabAppManifest GetApp(string id) =>
        Apps.FirstOrDefault(app => app.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Home Lab app '{id}' is not in the catalog.");

    public static HomeLabRecipeManifest GetRecipe(string id) =>
        Recipes.FirstOrDefault(recipe => recipe.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Home Lab recipe '{id}' is not in the catalog.");
}
