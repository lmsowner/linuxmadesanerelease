// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.HomeLab;

using LinuxMadeSane.Core.Models.HomeLab;

public sealed record HomeLabPromptRecipe(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> AppIds,
    bool RequiresVpnGateway,
    IReadOnlyList<string> VpnRoutedAppIds,
    string StarterPrompt,
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
    IReadOnlyList<string>? VpnRoutedApps = null);

public static class HomeLabPromptRecipeCatalog
{
    private const string OperatingContract =
        """
        Treat this as a guided Linux Made Sane Home Lab task. Use inspect_home_lab before proposing changes. Use apply_home_lab_prompt_recipe for supported deployments and repairs; do not replace the LMS-managed deployment with raw docker, docker compose, or shell commands. Never request or repeat passwords, API keys, VPN profiles, or other secrets in chat.

        LMS must remain the source of truth for container state, storage, local access routes, health checks, and network routing. Reuse healthy installed infrastructure where the selected recipe requires it. If required infrastructure is missing, direct me to the appropriate LMS form so credentials remain in protected fields. Inspect the result after changes and do not claim success until every requested app is present and LMS reports a usable health state.
        """;

    public static IReadOnlyList<HomeLabPromptRecipe> All { get; } =
    [
        Create(
            "wordpress-site",
            "WordPress website",
            "Create a WordPress instance with its managed database, persistent files, health checks, and an LMS local access route.",
            ["wordpress"],
            false,
            "Set up a private WordPress website with persistent site files and an LMS-managed database. Use the supported defaults unless I specify a WordPress version, variant, database choice, database administration tool, domain, or other requirement below.",
            "Keep WordPress and its database on the private LMS deployment network. Verify the database dependency before reporting the website ready."),
        Create(
            "forward-proxy",
            "Forward proxy server",
            "Create a private forward proxy for devices on networks you control and verify the proxy listener without publishing an internet-facing admin page.",
            ["squid-proxy"],
            false,
            "Set up a private forward proxy for devices on networks I control. Keep it private unless I explicitly describe a trusted network or access requirement.",
            "Treat this as a private network service. Do not expose it through Edge Gateway or configure it as an open public proxy."),
        Create(
            "secure-streaming",
            "Webtor and Stremio through VPN",
            "Set up Webtor and Stremio behind one reusable VPN Gateway, with LMS-managed local access and streaming connectivity.",
            ["webtor", "stremio-server"],
            true,
            "Set up Webtor and Stremio behind an existing VPN Gateway with LMS-managed local access. Ask me which gateway to use if there is more than one.",
            "Preserve trusted local Caddy access, Webtor HTTP cookie rewriting, separate internal ports in the shared VPN namespace, and the Stremio-to-host gateway mapping used for local Webtor streams. This is for personal, public-domain, or otherwise lawfully accessed content; do not configure content sources or add-ons."),
        Create(
            "secure-qbittorrent",
            "qBittorrent through VPN",
            "Set up qBittorrent behind an existing reusable VPN Gateway and verify that its traffic cannot bypass the gateway.",
            ["qbittorrent"],
            true,
            "Set up qBittorrent behind an existing VPN Gateway, store downloads in my LMS Home Lab storage, and verify that its traffic cannot bypass the gateway.",
            "Do not require inbound port forwarding: it is optional and some VPN providers support P2P without it. Preserve the trusted local LMS Caddy route and its first-open referrer handling."),
        Create(
            "vpn-media-automation",
            "qBittorrent and media automation through VPN",
            "Set up qBittorrent, Prowlarr, Sonarr, Radarr, and Seerr behind one reusable VPN Gateway.",
            ["qbittorrent", "prowlarr", "sonarr", "radarr", "seerr"],
            true,
            "Set up qBittorrent, Prowlarr, Sonarr, Radarr, and Seerr behind one existing VPN Gateway. Reuse my LMS Home Lab storage and ask before making any choice that depends on my environment.",
            "Route every requested service through the selected gateway and preserve a distinct internal listener for each web interface. Use the shared VPN namespace loopback endpoints when describing app-to-app connections. Do not select, recommend, or configure indexers, catalogues, download sources, or content. The user completes any source-specific configuration for services they are authorised to use."),
        Create(
            "jellyfin-local-library",
            "Jellyfin local media library",
            "Create a standalone Jellyfin server for movies and TV stored on this LMS host, with local network streaming and persistent configuration.",
            ["jellyfin"],
            false,
            "Set up Jellyfin for local network streaming from my LMS-managed movies and TV storage. Keep the media mounts read-only and let me add any library, client, transcoding, or access requirements below.",
            "Keep Jellyfin on its direct local LMS network route. Do not route Jellyfin through a VPN Gateway. Verify the movies and TV storage mounts, Jellyfin health endpoint, and LMS local access URL before reporting it ready."),
        Create(
            "jellyfin-arr-library",
            "Jellyfin with Sonarr and Radarr",
            "Create a local Jellyfin library alongside Sonarr and Radarr, sharing LMS-managed movie and TV storage.",
            ["jellyfin", "sonarr", "radarr"],
            false,
            "Set up Jellyfin with Sonarr and Radarr for a local movie and TV library. Reuse my LMS-managed movies and TV storage, keep Jellyfin local, and let me add precise library or automation requirements below.",
            "Keep Jellyfin on a direct local route and mount its movies and TV folders read-only. Give Sonarr and Radarr write access to their matching library folders. Report the LMS local URL for each app and explain that a download client and authorised indexer source are still needed before automated acquisition can work; do not invent or configure either."),
        Create(
            "jellyfin-local-automation",
            "Jellyfin with local media automation",
            "Create a complete direct-network Jellyfin library with qBittorrent, Prowlarr, Sonarr, Radarr, and Seerr.",
            ["jellyfin", "qbittorrent", "prowlarr", "sonarr", "radarr", "seerr"],
            false,
            "Set up a complete local Jellyfin media library with qBittorrent, Prowlarr, Sonarr, Radarr, and Seerr. Keep every app on a direct local route, reuse my LMS-managed downloads, movies, and TV storage, and let me add precise requirements below.",
            "Keep every app on a direct LMS route and preserve a distinct listener port for each web interface. Jellyfin reads the shared movies and TV folders while the automation services manage them. Report every LMS local URL and use those URLs when explaining how to connect Seerr, Sonarr, Radarr, Prowlarr, and qBittorrent. State clearly that qBittorrent traffic is not VPN protected in this recipe. Do not select, recommend, or configure indexers, catalogues, download sources, or content."),
        Create(
            "jellyfin-vpn-automation",
            "Jellyfin with VPN media automation",
            "Create a complete local Jellyfin library with qBittorrent, Prowlarr, Sonarr, Radarr, and Seerr, while keeping Jellyfin local and routing the automation services through one VPN Gateway.",
            ["jellyfin", "qbittorrent", "prowlarr", "sonarr", "radarr", "seerr"],
            true,
            "Set up a complete Jellyfin media library with qBittorrent, Prowlarr, Sonarr, Radarr, and Seerr. Keep Jellyfin available directly on my local network, route the download and automation services through one existing VPN Gateway, reuse my LMS-managed downloads, movies, and TV storage, and let me add precise requirements below.",
            "Never route Jellyfin through Gluetun: it is the local playback server and must use its direct LMS route. Route qBittorrent, Prowlarr, Sonarr, Radarr, and Seerr through the selected gateway, with a distinct listener port for every service. Jellyfin reads the shared movies and TV storage while the automation services manage those folders. Report every LMS local URL plus the shared VPN namespace loopback endpoints for app-to-app automation. Use Jellyfin's LMS local URL when explaining the Seerr connection. Do not select, recommend, or configure indexers, catalogues, download sources, or content.",
            ["qbittorrent", "prowlarr", "sonarr", "radarr", "seerr"]),
        Create(
            "private-photo-library",
            "Immich photo library",
            "Set up Immich and its managed database, cache, and machine-learning dependencies for a private photo and video library.",
            ["immich"],
            false,
            "Set up a private Immich photo and video library with persistent storage and its LMS-managed database, cache, and machine-learning services. Ask me about storage or access requirements that are not already configured.",
            "Verify every managed Immich dependency and the LMS local access route before reporting the library ready.")
    ];

    public static HomeLabPromptRecipe Get(string id) =>
        All.FirstOrDefault(recipe => recipe.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Home Lab prompt recipe '{id}' is not available.");

    public static HomeLabPromptRecipe CreatePublished(PublishedHomeLabPromptRecipe definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var id = definition.Id?.Trim() ?? string.Empty;
        var name = definition.RecipeName?.Trim() ?? string.Empty;
        var description = definition.Description?.Trim() ?? string.Empty;
        var basePrompt = definition.BaseRecipePrompt?.Trim() ?? string.Empty;
        var technicalGuidance = definition.TechnicalGuidance?.Trim() ?? string.Empty;
        var appIds = (definition.AppsBeingDeployed ?? [])
            .Select(appId => appId?.Trim() ?? string.Empty)
            .Where(appId => appId.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var vpnRoutedAppIds = (definition.VpnRoutedApps ?? (definition.RequiresVpnGateway ? appIds : []))
            .Select(appId => appId?.Trim() ?? string.Empty)
            .Where(appId => appId.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (id.Length is < 1 or > 80 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidOperationException("Published Home Lab prompt recipe IDs must use 1-80 ASCII letters, numbers, hyphens, or underscores.");
        }
        if (name.Length is < 1 or > 160 || description.Length is < 1 or > 600 || basePrompt.Length is < 1 or > 4000 || technicalGuidance.Length > 8000)
        {
            throw new InvalidOperationException($"Published Home Lab prompt recipe '{id}' has an invalid text field length.");
        }
        if (appIds.Length is < 1 or > 20 || appIds.Any(appId => !HomeLabCatalog.Apps.Any(app => app.Id.Equals(appId, StringComparison.OrdinalIgnoreCase) && app.IsInstallable)))
        {
            throw new InvalidOperationException($"Published Home Lab prompt recipe '{id}' contains unsupported LMS app IDs.");
        }
        if (definition.RequiresVpnGateway != (vpnRoutedAppIds.Length > 0) ||
            vpnRoutedAppIds.Any(appId => !appIds.Contains(appId, StringComparer.OrdinalIgnoreCase)) ||
            vpnRoutedAppIds.Any(appId => !HomeLabCatalog.GetApp(appId).SupportsVpnGateway))
        {
            throw new InvalidOperationException($"Published Home Lab prompt recipe '{id}' has an invalid VPN app list.");
        }

        return Create(id, name, description, appIds, definition.RequiresVpnGateway, basePrompt, technicalGuidance, vpnRoutedAppIds);
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
            throw new InvalidOperationException("Describe what you want from this Home Lab prompt recipe.");
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

            Keep LMS as the source of truth. Do not use raw docker, docker compose, or shell commands to replace LMS-managed configuration. Never request or expose passwords, API keys, VPN profiles, temporary application passwords, or container log secrets. Never call repair_home_lab_installation for a healthy container or use it as the first response to an application-setting fault. For a confirmed non-secret setting fault, call repair_home_lab_app_config with one exact replacement from the inspected file; LMS will back it up, restart only that app, verify Docker health and local access, and roll back automatically if the check fails. Treat duplicate shared-namespace ports, stale VPN callbacks, incorrect VPN routing, and mismatched forwarded ports as LMS container or network faults. Call repair_home_lab_installation only when health or VPN security evidence supports that diagnosis. A VPN-routed app is repaired alone unless the gateway security check proves the shared namespace is faulty. If I say manual image, environment, or volume edits caused the fault, set restoreContainerSettings to true. Inspect again afterward and report the assigned listener ports, VPN security, forwarding status, and local access. Do not install another application or apply a prompt recipe while troubleshooting this container.
            """;
    }

    private static HomeLabPromptRecipe Create(
        string id,
        string name,
        string description,
        IReadOnlyList<string> appIds,
        bool requiresVpnGateway,
        string starterPrompt,
        string technicalGuidance,
        IReadOnlyList<string>? vpnRoutedAppIds = null) =>
        new(
            id,
            name,
            description,
            appIds,
            requiresVpnGateway,
            vpnRoutedAppIds ?? (requiresVpnGateway ? appIds : []),
            starterPrompt,
            $"""
            {OperatingContract}

            Requested prompt recipe: {id}
            Goal: {description}
            Target LMS app IDs: {string.Join(", ", appIds)}
            Recipe-specific requirements: {technicalGuidance}

            Start by inspecting Home Lab. Explain what already exists, resolve any required infrastructure choice, then ask for approval through the LMS apply tool. After the approved action completes, inspect again and report actual health, network security where applicable, and local access details.
            """);
}
