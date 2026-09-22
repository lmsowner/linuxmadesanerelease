// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.HomeLab;

public sealed record HomeLabPromptRecipe(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> AppIds,
    bool RequiresVpnGateway,
    string Prompt);

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
            "Keep WordPress and its database on the private LMS deployment network. Verify the database dependency before reporting the website ready."),
        Create(
            "forward-proxy",
            "Forward proxy server",
            "Create a private forward proxy for devices on networks you control and verify the proxy listener without publishing an internet-facing admin page.",
            ["squid-proxy"],
            false,
            "Treat this as a private network service. Do not expose it through Edge Gateway or configure it as an open public proxy."),
        Create(
            "secure-streaming",
            "Webtor and Stremio through VPN",
            "Set up Webtor and Stremio behind one reusable VPN Gateway, with LMS-managed local access and streaming connectivity.",
            ["webtor", "stremio-server"],
            true,
            "Preserve trusted local Caddy access, Webtor HTTP cookie rewriting, separate internal ports in the shared VPN namespace, and the Stremio-to-host gateway mapping used for local Webtor streams. This is for personal, public-domain, or otherwise lawfully accessed content; do not configure content sources or add-ons."),
        Create(
            "secure-qbittorrent",
            "qBittorrent through VPN",
            "Set up qBittorrent behind an existing reusable VPN Gateway and verify that its traffic cannot bypass the gateway.",
            ["qbittorrent"],
            true,
            "Do not require inbound port forwarding: it is optional and some VPN providers support P2P without it. Preserve the trusted local LMS Caddy route and its first-open referrer handling."),
        Create(
            "vpn-media-automation",
            "qBittorrent and media automation through VPN",
            "Set up qBittorrent, Prowlarr, Sonarr, Radarr, and Seerr behind one reusable VPN Gateway.",
            ["qbittorrent", "prowlarr", "sonarr", "radarr", "seerr"],
            true,
            "Route every requested service through the selected gateway and preserve a distinct internal listener for each web interface. Use the shared VPN namespace loopback endpoints when describing app-to-app connections. Do not select, recommend, or configure indexers, catalogues, download sources, or content. The user completes any source-specific configuration for services they are authorised to use."),
        Create(
            "private-photo-library",
            "Immich photo library",
            "Set up Immich and its managed database, cache, and machine-learning dependencies for a private photo and video library.",
            ["immich"],
            false,
            "Verify every managed Immich dependency and the LMS local access route before reporting the library ready.")
    ];

    public static HomeLabPromptRecipe Get(string id) =>
        All.FirstOrDefault(recipe => recipe.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Home Lab prompt recipe '{id}' is not available.");

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

    private static HomeLabPromptRecipe Create(
        string id,
        string name,
        string description,
        IReadOnlyList<string> appIds,
        bool requiresVpnGateway,
        string technicalGuidance) =>
        new(
            id,
            name,
            description,
            appIds,
            requiresVpnGateway,
            $"""
            {OperatingContract}

            Requested prompt recipe: {id}
            Goal: {description}
            Target LMS app IDs: {string.Join(", ", appIds)}
            Recipe-specific requirements: {technicalGuidance}

            Start by inspecting Home Lab. Explain what already exists, resolve any required infrastructure choice, then ask for approval through the LMS apply tool. After the approved action completes, inspect again and report actual health, network security where applicable, and local access details.
            """);
}
