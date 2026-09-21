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
            [
                new("http-proxy", 8888, Primary: true),
                new("routed-web", 8080),
                new("routed-stremio", 11470)
            ],
            [new("config", "/gluetun", HomeLabStorageKind.Configuration)],
            new Dictionary<string, string>(),
            [],
            new HomeLabHealthCheckManifest(DockerCommand: "wget -qO- http://127.0.0.1:9999/ >/dev/null || exit 1"),
            [
                new("configuration-mode", "Setup method", "select", true, Help: "Use Guided setup for Gluetun provider profiles, or paste the provider file you downloaded.", Options: ["Guided", "Paste provider config"]),
                new("provider", "Provider", "select", true, Help: "These provider names are Gluetun provider profiles; LMS does not implement the VPN protocol.", Options: ["ProtonVPN", "NordVPN", "Mullvad", "Surfshark", "Private Internet Access", "Custom WireGuard"]),
                new("protocol", "Protocol", "select", true, Help: "Choose the protocol supported by your provider credentials.", Options: ["WireGuard", "OpenVPN"]),
                new("vpn-config", "Provider configuration", "secret-textarea", true, Secret: true, Help: "Paste the complete .ovpn file or WireGuard INI downloaded from your VPN provider. It is stored as a secret and is never shown again."),
                new("server-countries", "Server countries", Help: "Optional comma-separated Gluetun server country filter, for example Netherlands, Switzerland."),
                new("wireguard-private-key", "WireGuard private key", "secret", true, Secret: true, Help: "Required for WireGuard. Stored in the LMS secret store."),
                new("wireguard-addresses", "WireGuard addresses", Required: true, Help: "The tunnel address from your provider, for example 10.2.0.2/32."),
                new("wireguard-public-key", "WireGuard server public key", Help: "Required for custom WireGuard when your provider does not supply a server profile."),
                new("wireguard-endpoint-ip", "WireGuard endpoint IP", Help: "Optional custom WireGuard endpoint IP."),
                new("wireguard-endpoint-port", "WireGuard endpoint port", Help: "Optional custom WireGuard endpoint port."),
                new("wireguard-preshared-key", "WireGuard preshared key", "secret", Secret: true),
                new("openvpn-username", "OpenVPN username", "secret", true, Secret: true, Help: "Required for provider OpenVPN profiles."),
                new("openvpn-password", "OpenVPN password", "secret", true, Secret: true, Help: "Stored in the LMS secret store and never shown again.")
            ],
            SupportsVpnGateway: false,
            DockerCapabilities: ["NET_ADMIN"],
            DockerDevices: ["/dev/net/tun:/dev/net/tun"]),
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
            [new("network-route", "Internet route", "select", true, Help: "Direct exposes qBittorrent through the deployment network. VPN Gateway routes its traffic through Gluetun.", Options: ["VPN Gateway (Gluetun)", "Direct (no VPN)"])],
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
            new Dictionary<string, string>(), [], null,
            [new("network-route", "Internet route", "select", true, Help: "Direct exposes Stremio without a VPN. VPN Gateway routes its traffic through Gluetun.", Options: ["VPN Gateway (Gluetun)", "Direct (no VPN)"])],
            SupportsVpnGateway: true),
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
            [new("library", "/data", HomeLabStorageKind.UserData, "photos")],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DB_HOSTNAME"] = "immich-database",
                ["DB_USERNAME"] = "postgres",
                ["DB_PASSWORD"] = "postgres",
                ["DB_DATABASE_NAME"] = "immich",
                ["REDIS_HOSTNAME"] = "immich-redis",
                ["IMMICH_MACHINE_LEARNING_URL"] = "http://immich-machine-learning:3003"
            },
            ["immich-database", "immich-redis", "immich-machine-learning"],
            new HomeLabHealthCheckManifest(HttpPath: "/api/server/ping", Port: 2283), [], IsInstallable: true),
        new(
            "immich-database",
            "Immich Database",
            "PostgreSQL and VectorChord database used by Immich.",
            HomeLabAppCategory.Infrastructure,
            "database",
            "https://immich.app/",
            "https://docs.immich.app/install/docker-compose",
            "ghcr.io/immich-app/postgres",
            "14-vectorchord0.4.3-pgvectors0.2.0",
            "1",
            [],
            [new("database", "/var/lib/postgresql/data", HomeLabStorageKind.Configuration)],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["POSTGRES_PASSWORD"] = "postgres",
                ["POSTGRES_USER"] = "postgres",
                ["POSTGRES_DB"] = "immich",
                ["POSTGRES_INITDB_ARGS"] = "--data-checksums"
            },
            [],
            new HomeLabHealthCheckManifest(DockerCommand: "pg_isready -U postgres -d immich"),
            [],
            IsSystemDependency: true),
        new(
            "immich-redis",
            "Immich Redis",
            "Valkey cache service used by Immich.",
            HomeLabAppCategory.Infrastructure,
            "database",
            "https://immich.app/",
            "https://docs.immich.app/install/docker-compose",
            "docker.io/valkey/valkey",
            "9",
            "1",
            [],
            [],
            new Dictionary<string, string>(),
            [],
            new HomeLabHealthCheckManifest(DockerCommand: "valkey-cli ping | grep -q PONG"),
            [],
            IsSystemDependency: true),
        new(
            "immich-machine-learning",
            "Immich Machine Learning",
            "Machine-learning service used by Immich for search and facial recognition.",
            HomeLabAppCategory.Infrastructure,
            "cpu",
            "https://immich.app/",
            "https://docs.immich.app/install/docker-compose",
            "ghcr.io/immich-app/immich-machine-learning",
            "release",
            "1",
            [],
            [new("cache", "/cache", HomeLabStorageKind.Cache)],
            new Dictionary<string, string>(),
            [],
            new HomeLabHealthCheckManifest(DockerCommand: "wget --no-verbose --tries=1 --spider http://127.0.0.1:3003/ping"),
            [],
            IsSystemDependency: true),
        new(
            "webtor",
            "Webtor",
            "Browser-based torrent streaming service.",
            HomeLabAppCategory.Utilities,
            "play",
            "https://webtor.io/",
            "https://webtor.io/",
            "ghcr.io/webtor-io/self-hosted",
            "latest",
            "1",
            [new("web", 8080, Primary: true)],
            [new("data", "/data", HomeLabStorageKind.UserData), new("database", "/pgdata", HomeLabStorageKind.Configuration)],
            new Dictionary<string, string>(), [], null,
            [new("network-route", "Internet route", "select", true, Help: "Direct exposes Webtor without a VPN. VPN Gateway routes its traffic through Gluetun.", Options: ["VPN Gateway (Gluetun)", "Direct (no VPN)"])],
            SupportsVpnGateway: true)
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
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PUID"] = "1000", ["PGID"] = "1000", ["TZ"] = "UTC" }, [], new HomeLabHealthCheckManifest(HttpPath: "/", Port: 9696), []),
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
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PUID"] = "1000", ["PGID"] = "1000", ["TZ"] = "UTC" }, [], new HomeLabHealthCheckManifest(HttpPath: "/", Port: 8989), []),
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
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PUID"] = "1000", ["PGID"] = "1000", ["TZ"] = "UTC" }, [], new HomeLabHealthCheckManifest(HttpPath: "/", Port: 7878), []),
        new(
            "seerr",
            "Seerr",
            "Request management UI for Sonarr and Radarr.",
            HomeLabAppCategory.Automation,
            "list",
            "https://seerr.dev/",
            "https://docs.seerr.dev/",
            "ghcr.io/seerr-team/seerr",
            "latest",
            "1",
            [new("web", 5055, Primary: true)],
            [new("config", "/app/config", HomeLabStorageKind.Configuration)],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PORT"] = "5055", ["TZ"] = "UTC" }, [], new HomeLabHealthCheckManifest(HttpPath: "/api/v1/settings/public", Port: 5055), [])
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
            RequiresVpnGateway: true),
        new(
            "private-stremio",
            "Private Stremio",
            "Stremio Server routed through the reusable VPN Gateway.",
            ["vpn-gateway", "stremio-server"],
            [],
            [new("stremio-server", RouteVia: "vpn-gateway")],
            RequiresVpnGateway: true),
        new(
            "private-webtor",
            "Private Webtor",
            "Webtor routed through the reusable VPN Gateway.",
            ["vpn-gateway", "webtor"],
            [],
            [new("webtor", RouteVia: "vpn-gateway")],
            RequiresVpnGateway: true),
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
            IsInstallable: true),
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
            RequiresVpnGateway: true)
    ];

    public static HomeLabAppManifest GetApp(string id) =>
        Apps.FirstOrDefault(app => app.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Home Lab app '{id}' is not in the catalog.");

    public static IReadOnlyList<HomeLabAppManifest> VisibleApps =>
        Apps.Where(app => !app.IsSystemDependency).ToArray();

    public static HomeLabRecipeManifest GetRecipe(string id) =>
        Recipes.FirstOrDefault(recipe => recipe.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Home Lab recipe '{id}' is not in the catalog.");
}
