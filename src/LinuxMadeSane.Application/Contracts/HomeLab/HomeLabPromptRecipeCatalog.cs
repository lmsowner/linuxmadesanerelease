// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.HomeLab;

using LinuxMadeSane.Core.Models.HomeLab;

public sealed record HomeLabPromptRecipe(
    string Id,
    string Name,
    string Description,
    string Category,
    IReadOnlyList<string> Components,
    IReadOnlyList<string> AppIds,
    bool RequiresPlanning,
    bool RequiresVpnGateway,
    IReadOnlyList<string> VpnRoutedAppIds,
    string StarterPrompt,
    string TechnicalGuidance,
    string Prompt)
{
    public bool RoutesAppThroughVpn(string appId) =>
        VpnRoutedAppIds.Contains(appId, StringComparer.OrdinalIgnoreCase);
}

public sealed record PublishedHomeLabPromptRecipeCatalog(
    int SchemaVersion,
    IReadOnlyList<PublishedHomeLabPromptRecipe> Recipes);

public sealed record PublishedHomeLabPromptRecipe(
    string Id,
    string RecipeName,
    string Description,
    string BaseRecipePrompt,
    IReadOnlyList<string> AppsBeingDeployed,
    bool RequiresVpnGateway,
    string TechnicalGuidance,
    IReadOnlyList<string>? VpnRoutedApps = null,
    string? Category = null,
    IReadOnlyList<string>? Components = null,
    bool RequiresPlanning = false);

public static class HomeLabPromptRecipeCatalog
{
    private const string OperatingContract =
        """
        Treat this as a guided Linux Made Sane Home Lab task. Use inspect_home_lab before proposing changes. Use apply_home_lab_prompt_recipe for supported deployments and repairs; do not replace the LMS-managed deployment with raw docker, docker compose, or shell commands. Never request or repeat passwords, API keys, VPN profiles, or other secrets in chat.

        LMS must remain the source of truth for container state, storage, local access routes, health checks, and network routing. Reuse healthy installed infrastructure where the selected recipe requires it. If required infrastructure is missing, direct me to the appropriate LMS form so credentials remain in protected fields. Inspect the result after changes and do not claim success until every requested app is present and LMS reports a usable health state.
        """;

    public static IReadOnlyList<HomeLabPromptRecipe> All { get; } =
    [
        Plan("AI / Development", "local-ai-server", "Local AI Server",
            "Plan a private Ollama and Open WebUI server with hardware-aware acceleration.",
            ["Ollama", "Open WebUI", "GPU detection"],
            "Set up a private local AI server with Ollama and Open WebUI. Detect my available GPU and ask which model sizes and access policy I want before deployment.",
            "Confirm CPU-only, NVIDIA, AMD, or other acceleration and available memory. Do not claim GPU support until LMS can validate the runtime and device mapping."),
        Plan("AI / Development", "ai-coding-box", "AI Coding Box",
            "Plan a local coding assistant with a web UI, code models, and controlled Git access.",
            ["Open WebUI", "Ollama", "Code models", "Git integration"],
            "Set up an AI coding box with Open WebUI, Ollama, suitable local code models, and Git integration. Ask which repositories, languages, GPU, model size, and Git permissions I want.",
            "Treat repository access and Git credentials as protected configuration. Do not choose a model or grant write access without the user's requirements."),
        Plan("AI / Development", "private-git-platform", "Private Git Platform",
            "Plan a private Forgejo service with PostgreSQL and an explicitly registered runner.",
            ["Forgejo", "Forgejo Runner", "PostgreSQL"],
            "Set up a private Forgejo Git platform with PostgreSQL and a runner. Ask about the hostname, storage, runner workloads, registration token flow, and external access before deployment.",
            "Keep PostgreSQL private. Register the runner only after Forgejo is healthy and never place its registration token in chat."),
        Plan("AI / Development", "disposable-dev-environment", "Disposable Dev Environment",
            "Plan a browser-based development environment with Git and chosen language SDKs.",
            ["code-server", "Git", "Language SDKs"],
            "Set up a disposable code-server development environment with Git. Ask which language SDKs, versions, extensions, repository access, persistence, and resource limits I need.",
            "Do not guess the SDK image or persist Git credentials in the container definition."),

        Deploy("Media", "secure-streaming", "Instant Streaming",
            "Set up Webtor and Stremio behind one reusable VPN Gateway, with LMS-managed local access and streaming connectivity.",
            ["Webtor", "Stremio", "Gluetun"],
            ["webtor", "stremio-server"], true,
            "Set up Webtor and Stremio behind an existing VPN Gateway with LMS-managed local access. Ask me which gateway to use if there is more than one.",
            "Preserve Webtor cookie rewriting, distinct shared-namespace ports, and the Stremio-to-host mapping. Use only personal, public-domain, or otherwise lawfully accessed content; do not configure content sources or add-ons."),
        Deploy("Media", "jellyfin-vpn-automation", "Media Library",
            "Create a Jellyfin library with Seerr, Radarr, Sonarr, Prowlarr, and qBittorrent, while routing automation through Gluetun.",
            ["Jellyfin", "Jellyseerr / Seerr", "Radarr", "Sonarr", "Prowlarr", "qBittorrent", "Gluetun"],
            ["jellyfin", "qbittorrent", "prowlarr", "sonarr", "radarr", "seerr"], true,
            "Set up a complete local Jellyfin media library with Seerr, Radarr, Sonarr, Prowlarr, and qBittorrent. Keep Jellyfin local, route the automation services through one existing VPN Gateway, and reuse LMS-managed downloads, movies, and TV storage.",
            "Never route Jellyfin through Gluetun. Give each routed service a distinct listener and report both LMS local URLs and shared-namespace loopback endpoints. Do not select or configure indexers, catalogues, download sources, or content.",
            ["qbittorrent", "prowlarr", "sonarr", "radarr", "seerr"]),
        Plan("Media", "music-server", "Music Server",
            "Plan a Navidrome music library with an optional lawful download and import pipeline.",
            ["Navidrome", "Optional download/import pipeline"],
            "Set up Navidrome for my local music library. Ask about storage, metadata, users, external access, and whether I want an authorised download or import pipeline.",
            "Keep the library persistent and do not invent content sources. Treat the optional pipeline as a separate choice."),
        Deploy("Media", "private-photo-library", "Photo Library",
            "Set up Immich with its managed PostgreSQL, cache, and machine-learning services.",
            ["Immich", "PostgreSQL", "Redis / Valkey", "Machine learning"],
            ["immich"], false,
            "Set up a private Immich photo and video library with persistent storage, database, cache, and machine-learning services. Ask about storage and access requirements not already configured.",
            "Verify every managed dependency and the LMS local access route before reporting the library ready."),
        Deploy("Media", "secure-qbittorrent", "qBittorrent through VPN",
            "Set up qBittorrent behind a reusable VPN Gateway and verify its traffic cannot bypass the gateway.",
            ["qBittorrent", "Gluetun"],
            ["qbittorrent"], true,
            "Set up qBittorrent behind an existing VPN Gateway and store downloads in LMS Home Lab storage.",
            "Inbound port forwarding is optional. Preserve the LMS route and verify VPN security."),
        Deploy("Media", "vpn-media-automation", "Media Automation through VPN",
            "Set up qBittorrent, Prowlarr, Sonarr, Radarr, and Seerr behind one VPN Gateway.",
            ["qBittorrent", "Prowlarr", "Sonarr", "Radarr", "Jellyseerr / Seerr", "Gluetun"],
            ["qbittorrent", "prowlarr", "sonarr", "radarr", "seerr"], true,
            "Set up qBittorrent, Prowlarr, Sonarr, Radarr, and Seerr behind one existing VPN Gateway.",
            "Use distinct shared-namespace listeners. Do not select or configure indexers, catalogues, download sources, or content."),
        Deploy("Media", "jellyfin-local-library", "Jellyfin Local Library",
            "Create a standalone Jellyfin server for locally hosted movies and television.",
            ["Jellyfin"],
            ["jellyfin"], false,
            "Set up Jellyfin for local network streaming from LMS-managed movies and TV storage.",
            "Keep Jellyfin direct, with read-only media mounts, and verify its local route."),
        Deploy("Media", "jellyfin-arr-library", "Jellyfin with Sonarr and Radarr",
            "Create a local Jellyfin library alongside Sonarr and Radarr.",
            ["Jellyfin", "Sonarr", "Radarr"],
            ["jellyfin", "sonarr", "radarr"], false,
            "Set up Jellyfin with Sonarr and Radarr using LMS-managed movies and TV storage.",
            "Explain that a download client and authorised indexer source are still needed; do not invent either."),
        Deploy("Media", "jellyfin-local-automation", "Jellyfin with Local Automation",
            "Create a direct-network Jellyfin library with qBittorrent, Prowlarr, Sonarr, Radarr, and Seerr.",
            ["Jellyfin", "qBittorrent", "Prowlarr", "Sonarr", "Radarr", "Jellyseerr / Seerr"],
            ["jellyfin", "qbittorrent", "prowlarr", "sonarr", "radarr", "seerr"], false,
            "Set up a complete local Jellyfin media library and keep every app on a direct LMS route.",
            "State clearly that qBittorrent is not VPN protected. Do not configure indexers, catalogues, download sources, or content."),

        Plan("Networking", "private-vpn", "Private VPN",
            "Plan a WireGuard server for secure access to the home network.",
            ["WireGuard"],
            "Set up a private WireGuard VPN server. Ask about the LAN subnet, public endpoint, DNS, client count, router forwarding, and whether the host is behind CGNAT.",
            "Do not confuse a WireGuard server with the existing outbound Gluetun VPN Gateway."),
        Plan("Networking", "vpn-exit-node", "VPN Exit Node",
            "Plan a WireGuard exit node split between this server and a chosen VPS.",
            ["WireGuard", "VPS"],
            "Set up a WireGuard VPN exit node using a VPS. Ask which VPS, operating system, public address, routed subnets, DNS, and client platforms I use.",
            "This spans an external machine. Separate local LMS work from VPS commands and require the user to confirm the remote target."),
        Plan("Networking", "network-ad-blocking", "Network Ad Blocking",
            "Plan network-wide DNS filtering with AdGuard Home or Pi-hole and Unbound.",
            ["AdGuard Home or Pi-hole", "Unbound"],
            "Set up network ad blocking. Ask me to choose AdGuard Home or Pi-hole, then plan Unbound, a dedicated LAN address, router DNS changes, upstream policy, and recovery DNS.",
            "DNS must bind the correct LAN address and port 53 without colliding with the host resolver. Do not deploy both front ends."),
        Plan("Networking", "private-remote-access", "Private Remote Access",
            "Plan a private Headscale control plane and enrolment of Tailscale clients.",
            ["Headscale", "Tailscale clients"],
            "Set up private remote access with Headscale and Tailscale clients. Ask about the public hostname, certificates, users, routes, exit nodes, and each client platform.",
            "Client enrolment and auth keys occur after the control plane is reachable; never place reusable keys in chat."),
        Plan("Networking", "reverse-proxy", "Reverse Proxy",
            "Plan Caddy or Traefik routing with certificates and an explicit exposure policy.",
            ["Caddy or Traefik", "TLS certificates"],
            "Set up a reverse proxy. Ask me to choose Caddy or Traefik and provide domains, internal targets, certificate method, authentication, and exposure requirements.",
            "Inspect LMS Caddy and Edge Gateway first to avoid duplicate bindings on ports 80 and 443."),

        Plan("Home", "home-assistant", "Home Assistant",
            "Plan Home Assistant with MQTT and Zigbee device integration.",
            ["Home Assistant", "Mosquitto", "Zigbee2MQTT"],
            "Set up Home Assistant with Mosquitto and Zigbee2MQTT. Ask for my Zigbee adapter path, adapter type, MQTT credentials, discovery needs, timezone, and storage.",
            "Hardware discovery may require host networking or explicit devices. Validate the adapter before deploying Zigbee2MQTT."),
        Plan("Home", "cctv", "CCTV",
            "Plan Frigate with MQTT and validated GPU or Coral acceleration.",
            ["Frigate", "MQTT", "GPU or Coral detection"],
            "Set up Frigate CCTV with MQTT. Ask about cameras, retention, recording storage, detector hardware, GPU or Coral devices, and Home Assistant integration.",
            "Never guess camera credentials, stream URLs, or detector devices. Size storage before enabling recording."),
        Plan("Home", "energy-monitor", "Energy Monitor",
            "Plan energy collection and dashboards using Home Assistant, InfluxDB, and Grafana.",
            ["Home Assistant", "InfluxDB", "Grafana"],
            "Set up an energy monitoring stack with Home Assistant, InfluxDB, and Grafana. Ask which meters or integrations provide readings, retention requirements, units, and dashboards.",
            "Keep database credentials private and define retention before collecting high-frequency metrics."),

        Plan("Storage / Backup", "personal-cloud", "Personal Cloud",
            "Plan Nextcloud with a private database and Redis cache.",
            ["Nextcloud", "Database", "Redis"],
            "Set up a personal Nextcloud cloud with a private database and Redis. Ask about data storage, hostname, external access, mail, backups, and expected user count.",
            "Keep the database and Redis private and separate application data from database backups."),
        Plan("Storage / Backup", "dropbox-replacement", "Dropbox Replacement",
            "Plan device-to-device file synchronisation with Syncthing.",
            ["Syncthing"],
            "Set up Syncthing as a Dropbox replacement. Ask which folders, devices, versioning, ignore rules, storage paths, and remote access I need.",
            "Do not expose the admin interface publicly and do not share device IDs beyond the intended peers."),
        Plan("Storage / Backup", "backup-server", "Backup Server",
            "Plan Restic or Borg backups with an optional MinIO object target.",
            ["Restic or Borg", "MinIO"],
            "Set up a backup server. Ask me to choose Restic or Borg, identify source machines, retention, encryption, schedule, storage capacity, off-site copy, and whether MinIO is required.",
            "A backup is not complete until a restore test is defined. Keep repository keys out of chat."),
        Plan("Storage / Backup", "s3-server", "S3 Server",
            "Plan a private MinIO-compatible object store.",
            ["MinIO"],
            "Set up a private MinIO S3 server. Ask about data disks, erasure coding, hostname, TLS, users, bucket policy, backups, and external access.",
            "Do not deploy distributed or erasure-coded storage without enough independent disks."),

        Deploy("Privacy", "google-photos-replacement", "Google Photos Replacement",
            "Use Immich as a private photo and video library.",
            ["Immich", "PostgreSQL", "Redis / Valkey", "Machine learning"],
            ["immich"], false,
            "Set up Immich as my Google Photos replacement. Ask about library storage, mobile upload, import, users, backups, and external access.",
            "Verify all managed dependencies and explain that cloud export/import remains a separate user-authorised step."),
        Plan("Privacy", "google-drive-replacement", "Google Drive Replacement",
            "Plan Nextcloud as a private file sync and sharing service.",
            ["Nextcloud", "Database", "Redis"],
            "Set up Nextcloud as my Google Drive replacement. Ask about storage, desktop and mobile clients, sharing, office integration, backups, and external access.",
            "Keep its database and Redis private; do not claim existing cloud data has been migrated."),
        Plan("Privacy", "password-vault", "Password Vault",
            "Plan a private Vaultwarden service with backups and secure external access.",
            ["Vaultwarden"],
            "Set up Vaultwarden as a private password vault. Ask about hostname, TLS, account sign-up policy, email, backups, and whether access stays local or uses Edge Gateway.",
            "Require HTTPS for non-local use, disable open sign-ups after onboarding, and verify backup recovery."),
        Plan("Privacy", "private-search", "Private Search",
            "Plan a private metasearch service using SearXNG.",
            ["SearXNG"],
            "Set up a private SearXNG search service. Ask about enabled engines, rate limiting, language, hostname, authentication, and external access.",
            "Use a persistent secret key and do not expose an unrestricted public instance by default."),
        Plan("Privacy", "private-analytics", "Private Analytics",
            "Plan privacy-focused web analytics with Umami and PostgreSQL.",
            ["Umami", "PostgreSQL"],
            "Set up private Umami analytics with PostgreSQL. Ask about tracked sites, hostname, retention, backups, external access, and privacy settings.",
            "Keep PostgreSQL private and do not add tracking code to sites without explicit instruction."),

        Plan("Useful / Geeky", "internet-archive-box", "Internet Archive Box",
            "Plan a personal web archive using ArchiveBox.",
            ["ArchiveBox"],
            "Set up ArchiveBox. Ask about archive storage, import sources, crawl depth, browser dependencies, schedule, users, and access.",
            "Estimate storage and respect site permissions and applicable archiving rules."),
        Plan("Useful / Geeky", "rss-news-server", "RSS / News Server",
            "Plan a private FreshRSS feed reader.",
            ["FreshRSS"],
            "Set up FreshRSS. Ask about users, imports, refresh schedule, authentication, storage, and external access.",
            "Keep scheduled refreshes reasonable and protect external access."),
        Plan("Useful / Geeky", "pdf-toolkit", "PDF Toolkit",
            "Plan a private Stirling-PDF toolbox.",
            ["Stirling-PDF"],
            "Set up Stirling-PDF for private document processing. Ask about OCR languages, resource limits, authentication, storage, and external access.",
            "Treat uploaded documents as sensitive and avoid public anonymous exposure."),
        Plan("Useful / Geeky", "document-management", "Document Management",
            "Plan Paperless-ngx with database, cache, OCR, and document storage.",
            ["Paperless-ngx", "PostgreSQL", "Redis", "OCR"],
            "Set up Paperless-ngx. Ask about consume and archive folders, OCR languages, users, scanner workflow, retention, backups, and external access.",
            "Keep database and Redis private and validate writable document paths."),
        Plan("Useful / Geeky", "personal-wiki", "Personal Wiki",
            "Plan a private BookStack knowledge base.",
            ["BookStack", "Database"],
            "Set up BookStack as a personal wiki. Ask about hostname, users, database, attachments, mail, backups, and external access.",
            "Keep the database private and preserve both uploads and application keys."),
        Plan("Useful / Geeky", "status-page", "Status Page",
            "Plan service monitoring and a status dashboard with Uptime Kuma.",
            ["Uptime Kuma"],
            "Set up Uptime Kuma. Ask which services to monitor, check intervals, notification channels, status-page visibility, storage, and external access.",
            "Do not place notification secrets in chat and avoid publishing internal target details."),
        Plan("Useful / Geeky", "speed-test-history", "Speed Test History",
            "Plan scheduled connection tests and history using Speedtest Tracker.",
            ["Speedtest Tracker"],
            "Set up Speedtest Tracker. Ask about schedule, server selection, retention, notifications, timezone, storage, and external access.",
            "Avoid an excessive test schedule that distorts bandwidth use."),
        Deploy("Useful / Geeky", "wordpress-site", "WordPress Website",
            "Create WordPress with its managed database and persistent site files.",
            ["WordPress", "MariaDB"],
            ["wordpress"], false,
            "Set up a private WordPress website. Let me specify a version, variant, database choice, administration tool, domain, or other requirement.",
            "Keep the managed database private and explain unsupported variants before applying."),
        Deploy("Useful / Geeky", "forward-proxy", "Forward Proxy Server",
            "Create a private Squid forward proxy for networks the user controls.",
            ["Squid"],
            ["squid-proxy"], false,
            "Set up a private forward proxy for devices on networks I control.",
            "Do not expose it through Edge Gateway or configure an open public proxy."),

        Plan("Gaming", "retro-gaming-library", "Retro Gaming Library",
            "Plan a RomM library with browser emulation.",
            ["RomM", "Browser emulation"],
            "Set up a RomM retro gaming library with browser emulation. Ask about ROM storage, metadata providers, users, save storage, controllers, and access.",
            "The user supplies lawfully obtained game files and any metadata credentials through protected fields."),
        Plan("Gaming", "private-game-store", "Private Game Store",
            "Plan a private LANCommander game library.",
            ["LANCommander"],
            "Set up LANCommander as a private game store. Ask about library storage, users, authentication, game imports, and LAN-only or remote access.",
            "Do not source or distribute game files; the user provides authorised installers."),
        Plan("Gaming", "cloud-gaming-server", "Cloud Gaming Server",
            "Plan Sunshine streaming with Moonlight clients and validated graphics hardware.",
            ["Sunshine", "Moonlight clients"],
            "Set up a Sunshine cloud gaming server for Moonlight clients. Ask about GPU, display session, audio, input devices, games, network, resolution, and remote access.",
            "Validate hardware encoding, display access, and host device mappings before deployment."),
        Plan("Gaming", "minecraft-server", "Minecraft Server",
            "Plan a Java, Bedrock, or modded Minecraft server.",
            ["Minecraft Java, Bedrock, or modded server"],
            "Set up a Minecraft server. Ask me to choose Java, Bedrock, or a modpack and provide version, player count, world source, memory, backups, allowlist, and access requirements.",
            "Do not choose an edition, accept third-party mod terms, or expose a port until the user specifies the variant."),
        Plan("Gaming", "steam-dedicated-server", "Steam Dedicated Server",
            "Plan one explicitly selected SteamCMD-supported dedicated game server.",
            ["SteamCMD", "Selected dedicated game server"],
            "Set up a Steam dedicated server. Ask which supported game and then collect its app ID, ports, anonymous or authenticated install mode, player count, storage, config, backups, and access.",
            "Never deploy a generic idle SteamCMD container or guess a game. Protect any Steam credentials."),
        Plan("Gaming", "valheim-server", "Valheim Server",
            "Plan a persistent Valheim dedicated server.",
            ["Valheim dedicated server"],
            "Set up a Valheim server. Ask about world name or import, server name, password handling, player count, crossplay, mods, backups, updates, and access.",
            "Keep the server password in protected configuration and verify every required UDP port."),
        Plan("Gaming", "factorio-server", "Factorio Server",
            "Plan a persistent Factorio dedicated server.",
            ["Factorio dedicated server"],
            "Set up a Factorio server. Ask about game version, save import or generation, visibility, player count, mods, credentials, backups, updates, and access.",
            "Protect account tokens and preserve saves before updates."),
        Plan("Gaming", "satisfactory-server", "Satisfactory Server",
            "Plan a persistent Satisfactory dedicated server.",
            ["Satisfactory dedicated server"],
            "Set up a Satisfactory server. Ask about save import, player count, server claiming, storage, backups, updates, and LAN or internet access.",
            "Validate the required TCP and UDP listeners without colliding with other containers."),
        Plan("Gaming", "terraria-server", "Terraria Server",
            "Plan a vanilla or modded Terraria dedicated server.",
            ["Terraria or tModLoader server"],
            "Set up a Terraria server. Ask about vanilla or tModLoader, version, world import or generation, difficulty, players, password handling, mods, backups, and access.",
            "Do not choose a mod loader or overwrite a world without explicit requirements."),
        Plan("Gaming", "lan-party", "LAN Party",
            "Plan LANCommander, voice chat, and explicitly selected game servers.",
            ["LANCommander", "Voice server", "Selected game servers"],
            "Set up a LAN party environment with LANCommander, a voice server, and game servers I select. Ask about games, clients, users, storage, voice choice, network, schedule, and teardown or persistence.",
            "Do not deploy every game template. Produce a port plan first and reject duplicate host or shared-namespace listeners.")
    ];

    public static HomeLabPromptRecipe Get(string id) =>
        All.FirstOrDefault(recipe => recipe.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"HomeLab Recipe '{id}' is not available.");

    public static HomeLabPromptRecipe CreatePublished(PublishedHomeLabPromptRecipe definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var id = definition.Id?.Trim() ?? string.Empty;
        var name = definition.RecipeName?.Trim() ?? string.Empty;
        var description = definition.Description?.Trim() ?? string.Empty;
        var category = definition.Category?.Trim() ?? "Other";
        var basePrompt = definition.BaseRecipePrompt?.Trim() ?? string.Empty;
        var technicalGuidance = definition.TechnicalGuidance?.Trim() ?? string.Empty;
        var appIds = (definition.AppsBeingDeployed ?? [])
            .Select(appId => appId?.Trim() ?? string.Empty)
            .Where(appId => appId.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var components = (definition.Components ?? appIds.Select(appId => HomeLabCatalog.GetApp(appId).Name).ToArray())
            .Select(component => component?.Trim() ?? string.Empty)
            .Where(component => component.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var vpnRoutedAppIds = (definition.VpnRoutedApps ?? (definition.RequiresVpnGateway ? appIds : []))
            .Select(appId => appId?.Trim() ?? string.Empty)
            .Where(appId => appId.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (id.Length is < 1 or > 80 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidOperationException("Published HomeLab Recipe IDs must use 1-80 ASCII letters, numbers, hyphens, or underscores.");
        }
        if (name.Length is < 1 or > 160 || category.Length is < 1 or > 80 || description.Length is < 1 or > 600 || basePrompt.Length is < 1 or > 4000 || technicalGuidance.Length > 8000 || components.Length is < 1 or > 30 || components.Any(component => component.Length > 120))
        {
            throw new InvalidOperationException($"Published HomeLab Recipe '{id}' has an invalid text field length.");
        }
        if (appIds.Length > 20 || (!definition.RequiresPlanning && appIds.Length == 0) || appIds.Any(appId => !HomeLabCatalog.Apps.Any(app => app.Id.Equals(appId, StringComparison.OrdinalIgnoreCase) && app.IsInstallable)))
        {
            throw new InvalidOperationException($"Published HomeLab Recipe '{id}' contains unsupported LMS app IDs.");
        }
        if (definition.RequiresVpnGateway != (vpnRoutedAppIds.Length > 0) ||
            vpnRoutedAppIds.Any(appId => !appIds.Contains(appId, StringComparer.OrdinalIgnoreCase)) ||
            vpnRoutedAppIds.Any(appId => !HomeLabCatalog.GetApp(appId).SupportsVpnGateway))
        {
            throw new InvalidOperationException($"Published HomeLab Recipe '{id}' has an invalid VPN app list.");
        }

        return Create(category, id, name, description, components, appIds, definition.RequiresPlanning, definition.RequiresVpnGateway, basePrompt, technicalGuidance, vpnRoutedAppIds);
    }

    public static string BuildCustomPrompt(string request)
    {
        var normalized = request?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("Describe what you want the Home Lab AI to set up.");
        }

        return $"""
            {OperatingContract}

            Custom Home Lab request:
            {normalized}

            Start by inspecting Home Lab. Explain what can be handled by the supported LMS Home Lab tools and what information is still needed. Prefer existing LMS-managed apps and infrastructure. Present every change for approval and verify the actual result.
            """;
    }

    public static string BuildRecipePrompt(HomeLabPromptRecipe recipe, string request)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var normalized = request?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("Describe what you want from this HomeLab Recipe.");
        }

        return $"""
            {recipe.Prompt}

            User-edited requirements for this recipe:
            {normalized}

            Treat the user-edited requirements as part of the requested outcome. Work out which details are supported by the LMS-managed recipe and current app catalog. Before proposing a change, clearly explain any requested version, variant, companion app, database, administration suite, or other detail that LMS cannot currently apply. Do not silently ignore, substitute, or claim to have completed an unsupported requirement.
            """;
    }

    public static string BuildTroubleshootingPrompt(HomeLabAppInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        return $"""
            Treat this as a focused troubleshooting session for one existing Linux Made Sane Home Lab Docker container.

            Target installation ID: {installation.Id}
            LMS app ID: {installation.AppId}
            Display name: {installation.DisplayName}
            Container name: {installation.ContainerName}
            Current LMS health: {installation.HealthState}
            Current health detail: {installation.HealthDetail}

            Call inspect_home_lab first and match the exact installation ID. Check the container health, its managed dependencies, the LMS local Caddy access route, Docker networking, and VPN namespace or kill-switch state where applicable. Ask me what symptom I see if I have not described it yet. Explain the evidence in plain language. When Docker is healthy but the application URL or behaviour is broken, call inspect_home_lab_app_config before proposing any container action. Use the returned redacted file content and hash to identify an application-level setting fault.

            Keep LMS as the source of truth. Do not use raw docker, docker compose, or shell commands to replace LMS-managed configuration. Never request or expose passwords, API keys, VPN profiles, temporary application passwords, or container log secrets. Never call repair_home_lab_installation for a healthy container or use it as the first response to an application-setting fault. For a confirmed non-secret setting fault, call repair_home_lab_app_config with one exact replacement from the inspected file; LMS will back it up, restart only that app, verify Docker health and local access, and roll back automatically if the check fails. Treat duplicate shared-namespace ports, stale VPN callbacks, incorrect VPN routing, and mismatched forwarded ports as LMS container or network faults. Call repair_home_lab_installation only when health or VPN security evidence supports that diagnosis. A VPN-routed app is repaired alone unless the gateway security check proves the shared namespace is faulty. If I say manual image, environment, or volume edits caused the fault, set restoreContainerSettings to true. Inspect again afterward and report the assigned listener ports, VPN security, forwarding status, and local access. Do not install another application or apply a HomeLab Recipe while troubleshooting this container.
            """;
    }

    private static HomeLabPromptRecipe Deploy(
        string category,
        string id,
        string name,
        string description,
        IReadOnlyList<string> components,
        IReadOnlyList<string> appIds,
        bool requiresVpnGateway,
        string starterPrompt,
        string technicalGuidance,
        IReadOnlyList<string>? vpnRoutedAppIds = null) =>
        Create(category, id, name, description, components, appIds, false, requiresVpnGateway, starterPrompt, technicalGuidance, vpnRoutedAppIds);

    private static HomeLabPromptRecipe Plan(
        string category,
        string id,
        string name,
        string description,
        IReadOnlyList<string> components,
        string starterPrompt,
        string technicalGuidance) =>
        Create(category, id, name, description, components, [], true, false, starterPrompt, technicalGuidance);

    private static HomeLabPromptRecipe Create(
        string category,
        string id,
        string name,
        string description,
        IReadOnlyList<string> components,
        IReadOnlyList<string> appIds,
        bool requiresPlanning,
        bool requiresVpnGateway,
        string starterPrompt,
        string technicalGuidance,
        IReadOnlyList<string>? vpnRoutedAppIds = null) =>
        new(
            id,
            name,
            description,
            category,
            components,
            appIds,
            requiresPlanning,
            requiresVpnGateway,
            vpnRoutedAppIds ?? (requiresVpnGateway ? appIds : []),
            starterPrompt,
            technicalGuidance,
            $"""
            {OperatingContract}

            Requested HomeLab Recipe: {id}
            Goal: {description}
            Recipe components: {string.Join(", ", components)}
            Target LMS app IDs: {(appIds.Count == 0 ? "none yet" : string.Join(", ", appIds))}
            Recipe-specific requirements: {technicalGuidance}

            {(requiresPlanning
                ? "Start by inspecting Home Lab. This recipe requires planning because LMS cannot yet deploy every component safely. Ask only for the missing choices, explain exactly which parts LMS supports, and do not call apply_home_lab_prompt_recipe or install a partial stack. Produce a concrete plan the user can review."
                : "Start by inspecting Home Lab. Explain what already exists, resolve any required infrastructure choice, then ask for approval through the LMS apply tool. After the approved action completes, inspect again and report actual health, network security where applicable, and local access details.")}
            """);
}
