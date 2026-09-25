// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.HomeLab;

namespace LinuxMadeSane.Application.Contracts.HomeLab;

public static class HomeLabCatalog
{
    private static IReadOnlyList<HomeLabAppManifest> BuiltInApps { get; } =
    [
        new(
            "vpn-gateway",
            "VPN Gateway",
            "Reusable VPN gateway for apps that need protected internet access.",
            HomeLabAppCategory.Infrastructure,
            "vpn",
            "https://github.com/qdm12/gluetun",
            "https://github.com/qdm12/gluetun-wiki",
            "qmcgaw/gluetun",
            "latest",
            "6",
            [],
            [new("config", "/gluetun", HomeLabStorageKind.Configuration)],
            new Dictionary<string, string>(),
            [],
            new HomeLabHealthCheckManifest(DockerCommand: "wget -qO- http://127.0.0.1:9999/ >/dev/null || exit 1"),
            [
                new("gateway-name", "Gateway name", Help: "A label such as ProtonVPN, Mullvad, or NordVPN so apps can select the right gateway."),
                new("configuration-mode", "Configuration method", "select", true, Help: "Pasting the configuration file downloaded from your VPN provider is recommended. Guided setup remains available for providers that supply separate credentials.", Options: ["Paste provider config", "Guided"]),
                new("provider", "Provider", "select", true, Help: "These provider names are Gluetun provider profiles; LMS does not implement the VPN protocol.", Options: ["ProtonVPN", "NordVPN", "Mullvad", "Surfshark", "Private Internet Access", "Custom WireGuard"]),
                new("protocol", "Protocol", "select", true, Help: "Choose the protocol supported by your provider credentials.", Options: ["WireGuard", "OpenVPN"]),
                new("vpn-config", "Paste VPN configuration file", "secret-textarea", true, Secret: true, Help: "Paste the complete .ovpn file or WireGuard INI downloaded from your VPN provider. LMS keeps the provider profile as a secret and resolves WireGuard endpoint hostnames each time the gateway starts, because Gluetun requires a literal IP address. When editing an installed gateway, leave this empty to keep its current configuration."),
                new("port-forwarding", "Incoming P2P port", "select", true, Help: "An inbound port improves qBittorrent peer reachability. Proton requires a paid P2P server profile with NAT-PMP enabled; PIA also supports automatic forwarding. Other providers can allow P2P traffic without offering an inbound port.", Options: [HomeLabVpnPortForwardingPlan.RequiredSelection, HomeLabVpnPortForwardingPlan.OffSelection]),
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
            [new(
                "web",
                8080,
                Primary: true,
                VpnContainerPort: HomeLabVpnPortForwardingPlan.QbittorrentVpnWebUiPort,
                VpnEnvironmentVariable: "WEBUI_PORT")],
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
            [new("network-route", "Internet route", "select", true, Help: "Direct exposes qBittorrent through the deployment network. VPN Gateway routes its traffic through the selected Gluetun gateway.", Options: ["VPN Gateway (Gluetun)", "Direct (no VPN)"]), new("vpn-gateway", "VPN gateway", "select", true, Help: "Choose which installed Gluetun gateway carries qBittorrent traffic.")],
            SupportsVpnGateway: true,
            Exposure: ClientExposure(HomeLabBasePathSupportMode.Transparent)),
        new(
            "wordpress",
            "WordPress",
            "Website and publishing platform with an LMS-managed private database.",
            HomeLabAppCategory.Utilities,
            "web",
            "https://wordpress.org/",
            "https://hub.docker.com/_/wordpress",
            "wordpress",
            "latest",
            "1",
            [new("web", 80, Primary: true)],
            [new("site", "/var/www/html", HomeLabStorageKind.UserData)],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["WORDPRESS_DB_HOST"] = "${endpoint:wordpress-database:internal:host}:${endpoint:wordpress-database:internal:port}",
                ["WORDPRESS_DB_USER"] = "lms_wordpress",
                ["WORDPRESS_DB_PASSWORD"] = "lms-wordpress-internal",
                ["WORDPRESS_DB_NAME"] = "lms_wordpress"
            },
            ["wordpress-database"],
            new HomeLabHealthCheckManifest(HttpPath: "/wp-admin/install.php", Port: 80, StartPeriodSeconds: 60),
            []),
        new(
            "wordpress-database",
            "WordPress Database",
            "Private MariaDB dependency managed with the WordPress deployment.",
            HomeLabAppCategory.Infrastructure,
            "database",
            "https://mariadb.org/",
            "https://hub.docker.com/_/mariadb",
            "mariadb",
            "11",
            "1",
            [new("database", 3306)],
            [new("database", "/var/lib/mysql", HomeLabStorageKind.Configuration)],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["MARIADB_DATABASE"] = "lms_wordpress",
                ["MARIADB_USER"] = "lms_wordpress",
                ["MARIADB_PASSWORD"] = "lms-wordpress-internal",
                ["MARIADB_ROOT_PASSWORD"] = "lms-wordpress-root-internal"
            },
            [],
            new HomeLabHealthCheckManifest(DockerCommand: "healthcheck.sh --connect --innodb_initialized", StartPeriodSeconds: 45),
            [],
            IsSystemDependency: true),
        new(
            "squid-proxy",
            "Squid Forward Proxy",
            "Private caching forward proxy for devices on networks you control.",
            HomeLabAppCategory.Infrastructure,
            "network",
            "https://www.squid-cache.org/",
            "https://hub.docker.com/r/ubuntu/squid",
            "ubuntu/squid",
            "latest",
            "1",
            [new("proxy", 3128, Primary: true, HostBindingAddress: "0.0.0.0")],
            [],
            new Dictionary<string, string>(),
            [],
            new HomeLabHealthCheckManifest(DockerCommand: "test -s /run/squid.pid && kill -0 \"$(cat /run/squid.pid)\"", StartPeriodSeconds: 30),
            [],
            SupportsVpnGateway: true,
            EdgeGatewaySupported: false,
            Exposure: new HomeLabServiceExposureManifest(
                [HomeLabEndpointScope.Internal, HomeLabEndpointScope.Lan, HomeLabEndpointScope.Client],
                new HomeLabClientAccessManifest(true, "proxy"))),
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
            "Self-hosted Stremio web player and streaming server.",
            HomeLabAppCategory.Media,
            "play",
            "https://www.stremio.com/",
            "https://github.com/tsaridas/stremio-docker",
            "tsaridas/stremio-docker",
            "latest",
            "4",
            [
                new("web", 8080, Primary: true, VpnContainerPort: 18081, VpnEnvironmentVariable: "WEBUI_INTERNAL_PORT"),
                new("stream", 11470)
            ],
            [new("config", "/root/.stremio-server", HomeLabStorageKind.Configuration)],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NO_CORS"] = "1",
                ["AUTO_SERVER_URL"] = "1",
                ["CASTING_DISABLED"] = "1"
            },
            [],
            new HomeLabHealthCheckManifest(HttpPath: "/settings", Port: 8080, StartPeriodSeconds: 60),
            [new("network-route", "Internet route", "select", true, Help: "Stremio must use VPN Gateway routing. LMS blocks direct Stremio installations.", Options: new[] { "VPN Gateway (Gluetun)" }), new("vpn-gateway", "VPN gateway", "select", true, Help: "Choose which installed Gluetun gateway carries Stremio traffic.")],
            SupportsVpnGateway: true,
            RequiresVpnGateway: true,
            Exposure: new HomeLabServiceExposureManifest(
                [HomeLabEndpointScope.Internal, HomeLabEndpointScope.Lan, HomeLabEndpointScope.Client, HomeLabEndpointScope.Public],
                new HomeLabClientAccessManifest(
                    true,
                    "web",
                    HomeLabClientRoutingStrategy.Subdomain,
                    "",
                    new HomeLabReverseProxyManifest(HomeLabBasePathSupportMode.None),
                    new HomeLabEndpointCapabilities(Streaming: true, RangeRequests: true, WebSockets: true, Uploads: true, LongLivedRequests: true)),
                [new HomeLabClientAccessManifest(
                    true,
                    "stream",
                    HomeLabClientRoutingStrategy.Subdomain,
                    "",
                    new HomeLabReverseProxyManifest(
                        HomeLabBasePathSupportMode.Transparent,
                        StripPathPrefix: true,
                        ForwardPathPrefix: false),
                    new HomeLabEndpointCapabilities(
                        WebSockets: true,
                        Streaming: true,
                        RangeRequests: true,
                        Uploads: true,
                        LongLivedRequests: true),
                    PublicUrlEnvironmentVariable: "SERVER_URL")])),
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
                ["DB_HOSTNAME"] = "${endpoint:immich-database:internal:host}",
                ["DB_USERNAME"] = "postgres",
                ["DB_PASSWORD"] = "postgres",
                ["DB_DATABASE_NAME"] = "immich",
                ["REDIS_HOSTNAME"] = "${endpoint:immich-redis:internal:host}",
                ["IMMICH_MACHINE_LEARNING_URL"] = "${endpoint:immich-machine-learning:internal}"
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
            [new("database", 5432)],
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
            [new("redis", 6379)],
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
            [new("api", 3003, ApplicationProtocol: "http")],
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
            "https://github.com/webtor-io/self-hosted",
            "ghcr.io/webtor-io/self-hosted",
            "latest",
            "4",
            [new(
                "web",
                8080,
                Primary: true,
                VpnContainerPort: 18080,
                VpnFileOverride: new("/etc/webtor/common.template.env", "WEB_PORT", "/init"))],
            [new("data", "/data", HomeLabStorageKind.UserData), new("database", "/pgdata", HomeLabStorageKind.Configuration)],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DOMAIN"] = "${endpoint:self:client}",
                ["DISABLE_VIDEO_TRANSCODING"] = "false"
            }, [], new HomeLabHealthCheckManifest(HttpPath: "/", Port: 8080, DockerCommand: "set -a; . /etc/webtor/common.env; PGPASSWORD=\"$PG_PASSWORD\" psql -U \"$PG_USER\" -d \"$PG_DATABASE\" -Atqc 'select 1' | grep -qx 1", StartPeriodSeconds: 45),
            [new("network-route", "Internet route", "select", true, Help: "Webtor must use VPN Gateway routing. LMS blocks direct Webtor installations.", Options: new[] { "VPN Gateway (Gluetun)" }), new("vpn-gateway", "VPN gateway", "select", true, Help: "Choose which installed Gluetun gateway carries Webtor traffic.")],
            SupportsVpnGateway: true,
            RequiresVpnGateway: true,
            Exposure: ClientExposure(
                HomeLabBasePathSupportMode.Native,
                streaming: true,
                rangeRequests: true,
                webSockets: true,
                stripPathPrefix: true,
                rewriteSecureCookiesForHttp: true)),
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
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PUID"] = "1000", ["PGID"] = "1000", ["TZ"] = "UTC" }, [], new HomeLabHealthCheckManifest(HttpPath: "/", Port: 9696),
            [new("network-route", "Internet route", "select", true, Help: "Choose direct access or an installed VPN Gateway.", Options: ["VPN Gateway (Gluetun)", "Direct (no VPN)"]), new("vpn-gateway", "VPN gateway", "select", true, Help: "Choose which installed gateway carries Prowlarr traffic.")],
            SupportsVpnGateway: true,
            Exposure: ArrClientExposure()),
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
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PUID"] = "1000", ["PGID"] = "1000", ["TZ"] = "UTC" }, [], new HomeLabHealthCheckManifest(HttpPath: "/", Port: 8989),
            [new("network-route", "Internet route", "select", true, Help: "Choose direct access or an installed VPN Gateway.", Options: ["VPN Gateway (Gluetun)", "Direct (no VPN)"]), new("vpn-gateway", "VPN gateway", "select", true, Help: "Choose which installed gateway carries Sonarr traffic.")],
            SupportsVpnGateway: true,
            Exposure: ArrClientExposure()),
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
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PUID"] = "1000", ["PGID"] = "1000", ["TZ"] = "UTC" }, [], new HomeLabHealthCheckManifest(HttpPath: "/", Port: 7878),
            [new("network-route", "Internet route", "select", true, Help: "Choose direct access or an installed VPN Gateway.", Options: ["VPN Gateway (Gluetun)", "Direct (no VPN)"]), new("vpn-gateway", "VPN gateway", "select", true, Help: "Choose which installed gateway carries Radarr traffic.")],
            SupportsVpnGateway: true,
            Exposure: ArrClientExposure()),
        new(
            "seerr",
            "Seerr (Overseerr successor)",
            "Request management UI for Sonarr and Radarr, succeeding Overseerr and Jellyseerr.",
            HomeLabAppCategory.Automation,
            "list",
            "https://seerr.dev/",
            "https://docs.seerr.dev/",
            "ghcr.io/seerr-team/seerr",
            "latest",
            "1",
            [new("web", 5055, Primary: true)],
            [new("config", "/app/config", HomeLabStorageKind.Configuration, HostOwner: "1000:1000")],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PORT"] = "5055", ["TZ"] = "UTC" }, [], new HomeLabHealthCheckManifest(HttpPath: "/api/v1/settings/public", Port: 5055),
            [new("network-route", "Internet route", "select", true, Help: "Choose direct access or an installed VPN Gateway.", Options: ["VPN Gateway (Gluetun)", "Direct (no VPN)"]), new("vpn-gateway", "VPN gateway", "select", true, Help: "Choose which installed gateway carries Seerr traffic.")],
            SupportsVpnGateway: true)
    ];

    private static IReadOnlyList<HomeLabRecipeManifest> BuiltInRecipes { get; } =
    [
        new(
            "secure-streaming",
            "Secure Streaming",
            "Stremio and Webtor with browser-safe generated endpoints behind a VPN Gateway.",
            ["vpn-gateway", "stremio-server", "webtor"],
            [],
            [
                new("stremio-server", RouteVia: "vpn-gateway"),
                new("webtor", RouteVia: "vpn-gateway")
            ],
            RequiresVpnGateway: true,
            ConnectivityDependencies:
            [
                new(
                    "stremio-server",
                    "webtor",
                    HomeLabDependencyAccessFrom.Client,
                    "Stremio browser add-on access",
                    "${endpoint:webtor:client}")
            ]),
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
            IsInstallable: true,
            ConnectivityDependencies:
            [
                new("sonarr", "qbittorrent", HomeLabDependencyAccessFrom.Service, "Download client"),
                new("sonarr", "prowlarr", HomeLabDependencyAccessFrom.Service, "Indexer manager"),
                new("radarr", "qbittorrent", HomeLabDependencyAccessFrom.Service, "Download client"),
                new("radarr", "prowlarr", HomeLabDependencyAccessFrom.Service, "Indexer manager"),
                new("seerr", "sonarr", HomeLabDependencyAccessFrom.Service, "TV manager"),
                new("seerr", "radarr", HomeLabDependencyAccessFrom.Service, "Movie manager")
            ]),
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

    public static void Configure(string userCatalogRoot, string defaultCatalogRoot) =>
        HomeLabCatalogFileStore.Configure(userCatalogRoot, defaultCatalogRoot);

    public static bool IsConfigured => HomeLabCatalogFileStore.IsConfigured;

    public static IReadOnlyList<HomeLabAppManifest> Apps =>
        HomeLabCatalogFileStore.LoadApps(BuiltInApps);

    public static IReadOnlyList<HomeLabRecipeManifest> Recipes =>
        HomeLabCatalogFileStore.LoadRecipes(BuiltInRecipes);

    public static HomeLabAppManifest GetApp(string id) =>
        Apps.FirstOrDefault(app => app.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Home Lab app '{id}' is not in the catalog.");

    public static IReadOnlyList<HomeLabAppManifest> VisibleApps =>
        Apps.Where(app => !app.IsSystemDependency).ToArray();

    public static HomeLabRecipeManifest GetRecipe(string id) =>
        Recipes.FirstOrDefault(recipe => recipe.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Home Lab recipe '{id}' is not in the catalog.");

    private static HomeLabServiceExposureManifest ClientExposure(
        HomeLabBasePathSupportMode basePathSupport,
        bool streaming = false,
        bool rangeRequests = false,
        bool webSockets = false,
        bool? stripPathPrefix = null,
        bool rewriteSecureCookiesForHttp = false) =>
        new(
            [HomeLabEndpointScope.Internal, HomeLabEndpointScope.Lan, HomeLabEndpointScope.Client, HomeLabEndpointScope.Public],
            new HomeLabClientAccessManifest(
                true,
                "web",
                HomeLabClientRoutingStrategy.Subdomain,
                "",
                new HomeLabReverseProxyManifest(
                    basePathSupport,
                    StripPathPrefix: stripPathPrefix ?? (basePathSupport != HomeLabBasePathSupportMode.Native),
                    ForwardPathPrefix: true,
                    RewriteSecureCookiesForHttp: rewriteSecureCookiesForHttp),
                new HomeLabEndpointCapabilities(
                    WebSockets: webSockets,
                    Streaming: streaming,
                    RangeRequests: rangeRequests,
                    Uploads: true,
                    LongLivedRequests: streaming)));

    private static HomeLabServiceExposureManifest ArrClientExposure() =>
        new(
            [HomeLabEndpointScope.Internal, HomeLabEndpointScope.Lan, HomeLabEndpointScope.Client, HomeLabEndpointScope.Public],
            new HomeLabClientAccessManifest(
                true,
                "web",
                HomeLabClientRoutingStrategy.Subdomain,
                "",
                new HomeLabReverseProxyManifest(
                    HomeLabBasePathSupportMode.Native,
                    StripPathPrefix: false,
                    ForwardPathPrefix: true,
                    Configuration: new HomeLabBasePathConfiguration(
                        FilePath: "/config/config.xml",
                        XmlElement: "UrlBase")),
                new HomeLabEndpointCapabilities(WebSockets: true, Uploads: true)));
}
