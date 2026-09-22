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
        Treat this as a guided Linux Made Sane Home Lab task. Use inspect_home_lab before proposing changes. Use apply_home_lab_prompt_recipe for deployment and repair; do not replace the LMS-managed deployment with raw docker, docker compose, or shell commands. Reuse a healthy installed VPN Gateway when the recipe requires one. If no gateway exists, tell me to configure VPN Gateway in Home Lab > Apps so provider credentials stay in LMS secret fields. If several gateways are available, ask me which one to use. Never request or repeat VPN credentials in chat.

        LMS must remain the source of truth for container state, storage, Caddy access, health checks, and network routing. The apply tool must preserve the fixes LMS provides, including trusted local Caddy access, Webtor HTTP cookie rewriting, app-specific internal ports in a shared VPN namespace, and the Stremio-to-host gateway mapping used for local Webtor streams. Inspect the result after changes. Do not claim success unless each requested app is present, its required VPN route is secured, and LMS reports a usable health state. If an existing container is faulty, use the LMS repair path through the apply tool.

        This setup is for personal, public-domain, or otherwise lawfully accessed content. Do not find, recommend, or configure infringing content sources, indexers, catalogues, or add-ons.
        """;

    public static IReadOnlyList<HomeLabPromptRecipe> All { get; } =
    [
        Create(
            "secure-streaming",
            "Secure Webtor and Stremio",
            "Set up Webtor and Stremio behind one reusable VPN Gateway, with LMS-managed local access and streaming connectivity.",
            ["webtor", "stremio-server"],
            true),
        Create(
            "secure-webtor",
            "Secure Webtor",
            "Set up Webtor behind an existing reusable VPN Gateway and verify its LMS Caddy route.",
            ["webtor"],
            true),
        Create(
            "secure-stremio",
            "Secure Stremio",
            "Set up Stremio Server behind an existing reusable VPN Gateway and preserve local stream access.",
            ["stremio-server"],
            true),
        Create(
            "secure-qbittorrent",
            "Secure qBittorrent",
            "Set up qBittorrent behind an existing reusable VPN Gateway without requiring inbound port forwarding.",
            ["qbittorrent"],
            true),
        Create(
            "personal-media-server",
            "Personal media server",
            "Set up Jellyfin for a personal or otherwise lawfully managed media library using LMS storage and access routes.",
            ["jellyfin"],
            false),
        Create(
            "private-photo-library",
            "Private photo library",
            "Set up Immich and its managed dependencies for a private photo and video library.",
            ["immich"],
            false)
    ];

    public static HomeLabPromptRecipe Get(string id) =>
        All.FirstOrDefault(recipe => recipe.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Home Lab prompt recipe '{id}' is not available.");

    private static HomeLabPromptRecipe Create(
        string id,
        string name,
        string description,
        IReadOnlyList<string> appIds,
        bool requiresVpnGateway) =>
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

            Start by inspecting Home Lab. Explain what already exists, resolve the VPN Gateway choice if one is required, then ask for approval through the LMS apply tool. After the approved action completes, inspect again and report the actual app health, VPN security state, and LMS local access routes.
            """);
}
