namespace LinuxMadeSane.Web.Components.Ui;

public static class ThemePaletteOptionCatalog
{
    public static IReadOnlyList<ThemePaletteOption> Options { get; } =
    [
        new("amethyst-haze", "Amethyst Haze"),
        new("arctic-mint", "Arctic Mint"),
        new("aurora-berry", "Aurora Berry"),
        new("cherry-glass", "Cherry Glass"),
        new("citron-ice", "Citron Ice"),
        new("cobalt-frost", "Cobalt Frost"),
        new("copper-cloud", "Copper Cloud"),
        new("coral-fizz", "Coral Fizz"),
        new("crimson-theatre", "Crimson Theatre"),
        new("ember-signal", "Ember Signal"),
        new("emerald-echo", "Emerald Echo"),
        new("forest-static", "Forest Static"),
        new("glacier-cyan", "Glacier Cyan"),
        new("jade-pulse", "Jade Pulse"),
        new("lime-vault", "Lime Vault"),
        new("midnight-plum", "Midnight Plum"),
        new("molten-sunset", "Molten Sunset"),
        new("monsoon-teal", "Monsoon Teal"),
        new("moon-silver", "Moon Silver"),
        new("neon-orchid", "Neon Orchid"),
        new("obsidian-rose", "Obsidian Rose"),
        new("ocean-current", "Ocean Current"),
        new("peach-voltage", "Peach Voltage"),
        new("polar-lilac", "Polar Lilac"),
        new("rose-circuit", "Rose Circuit"),
        new("ruby-noir", "Ruby Noir"),
        new("sandstorm-gold", "Sandstorm Gold"),
        new("smooth-grey", "Smooth Grey"),
        new("solar-flare", "Solar Flare"),
        new("storm-indigo", "Storm Indigo"),
        new("violet-arc", "Violet Arc")
    ];

    public sealed record ThemePaletteOption(string Id, string Name);
}
