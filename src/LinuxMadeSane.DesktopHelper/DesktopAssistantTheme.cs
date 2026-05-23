// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using Avalonia.Media;
using LinuxMadeSane.Core.Models.DesktopSession;

namespace LinuxMadeSane.DesktopHelper;

internal sealed record DesktopAssistantTheme(
    Color Base,
    Color BaseAlt,
    Color Panel,
    Color PanelStrong,
    Color PanelSoft,
    Color Field,
    Color Line,
    Color Text,
    Color Muted,
    Color Accent,
    Color AccentBright,
    Color AccentDeep,
    Color OnAccent,
    Color Warning,
    Color Danger,
    Color AssistantBubble,
    Color UserBubble,
    Color Approval)
{
    private static readonly IReadOnlyDictionary<string, Palette> Palettes =
        new[]
        {
            new Palette("amethyst-haze", "#a96cff", "#ff8bd7"),
            new Palette("arctic-mint", "#49dcb1", "#8ff7ff"),
            new Palette("aurora-berry", "#7a5cff", "#4ce5b1"),
            new Palette("cherry-glass", "#ff4d6d", "#ff9e7a"),
            new Palette("citron-ice", "#b7d94c", "#62e7ff"),
            new Palette("cobalt-frost", "#2f5fff", "#a8ddff"),
            new Palette("copper-cloud", "#c98352", "#ffd27a"),
            new Palette("coral-fizz", "#ff7f6a", "#ffd36f"),
            new Palette("crimson-theatre", "#ff5b8a", "#ff7d5a"),
            new Palette("ember-signal", "#ff7b54", "#ffb36b"),
            new Palette("emerald-echo", "#20c997", "#96f56f"),
            new Palette("forest-static", "#4f9f3d", "#b4ef72"),
            new Palette("glacier-cyan", "#3dc7ff", "#9ff3ff"),
            new Palette("jade-pulse", "#0fbf7f", "#8cffc4"),
            new Palette("lime-vault", "#8fd14f", "#dcff78"),
            new Palette("midnight-plum", "#8b5cf6", "#f472b6"),
            new Palette("molten-sunset", "#ff5c7c", "#ffae42"),
            new Palette("monsoon-teal", "#1ea7a1", "#6fe6d8"),
            new Palette("moon-silver", "#8b9bb4", "#dbe6ff"),
            new Palette("neon-orchid", "#d867ff", "#73f3ff"),
            new Palette("obsidian-rose", "#7f4d66", "#ff87b5"),
            new Palette("ocean-current", "#2e7bff", "#5be7ff"),
            new Palette("peach-voltage", "#ff9a5c", "#ff6fcf"),
            new Palette("polar-lilac", "#c1a6ff", "#8fe7ff"),
            new Palette("rose-circuit", "#ff78b3", "#b46cff"),
            new Palette("ruby-noir", "#e54868", "#8d4fff"),
            new Palette("sandstorm-gold", "#d9a441", "#fff29a"),
            new Palette("smooth-grey", "#0a84ff", "#8e8e93", true),
            new Palette("solar-flare", "#ffb100", "#ff5f45"),
            new Palette("storm-indigo", "#536dfe", "#7dc3ff"),
            new Palette("violet-arc", "#7d3cff", "#c77dff")
        }.ToDictionary(palette => palette.Id, StringComparer.OrdinalIgnoreCase);

    public static DesktopAssistantTheme From(DesktopAssistantNativeTheme? theme)
    {
        var paletteId = string.IsNullOrWhiteSpace(theme?.PaletteId)
            ? "smooth-grey"
            : theme.PaletteId;
        var palette = Palettes.GetValueOrDefault(paletteId) ?? Palettes["smooth-grey"];
        var scheme = ResolveScheme(theme?.Mode, theme?.Scheme);
        return palette.SmoothGrey
            ? BuildSmoothGrey(scheme)
            : BuildDefault(palette, scheme);
    }

    public IBrush Brush(Color color, double opacity = 1)
    {
        var alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    private static DesktopAssistantTheme BuildSmoothGrey(string scheme)
    {
        var dark = scheme == "dark";
        var accent = Parse(dark ? "#0a84ff" : "#007aff");
        var accentBright = Parse(dark ? "#5ac8fa" : "#409cff");
        var accentDeep = Parse(dark ? "#355c8a" : "#3a5f94");
        var baseColor = Parse(dark ? "#111214" : "#eef0f3");
        var panel = Parse(dark ? "#1e1e20" : "#ffffff");
        var panelStrong = Parse(dark ? "#242426" : "#ffffff");
        var panelSoft = Parse(dark ? "#2c2c2e" : "#f5f5f7");
        var text = Parse(dark ? "#f5f5f7" : "#1d1d1f");
        var muted = Parse(dark ? "#aeaeb2" : "#6e6e73");

        return new DesktopAssistantTheme(
            baseColor,
            Parse(dark ? "#1c1c1e" : "#f5f5f7"),
            panel,
            panelStrong,
            panelSoft,
            Parse(dark ? "#2c2c2e" : "#ffffff"),
            Parse(dark ? "#56565d" : "#d3d4da"),
            text,
            muted,
            accent,
            accentBright,
            accentDeep,
            Parse(dark ? "#081018" : "#ffffff"),
            Parse(dark ? "#ffd60a" : "#b78103"),
            Parse(dark ? "#ff453a" : "#d92c20"),
            Parse(dark ? "#242426" : "#ffffff"),
            Mix(accentDeep, dark ? Parse("#0f1724") : Parse("#eff6ff"), 0.48),
            Parse(dark ? "#33270d" : "#fff5d6"));
    }

    private static DesktopAssistantTheme BuildDefault(Palette palette, string scheme)
    {
        var dark = scheme == "dark";
        var accent = Parse(palette.Accent);
        var accentAlt = Parse(palette.AccentAlt);
        var accentCore = Mix(accent, accentAlt, 0.18);
        var accentDeep = dark
            ? Mix(Parse("#03040a"), accentCore, 0.82)
            : Mix(Parse("#1a2233"), accentCore, 0.72);
        var accentBright = dark
            ? Mix(Parse("#ffffff"), accentAlt, 0.34)
            : Mix(Parse("#1f2937"), accentAlt, 0.42);
        var base950 = dark
            ? Mix(Parse("#020308"), accentDeep, 0.66)
            : Mix(Parse("#f4f6fb"), accentAlt, 0.12);
        var base900 = dark
            ? Mix(Parse("#070d17"), accent, 0.52)
            : Mix(Parse("#ffffff"), accentAlt, 0.08);
        var panel = dark
            ? Mix(Parse("#0c1220"), accentDeep, 0.52)
            : Mix(Parse("#ffffff"), accentAlt, 0.08);
        var panelStrong = dark
            ? Mix(Parse("#080d18"), accentDeep, 0.62)
            : Mix(Parse("#ffffff"), accent, 0.06);
        var panelSoft = dark
            ? Mix(Parse("#101726"), accentAlt, 0.38)
            : Mix(Parse("#ffffff"), accentAlt, 0.08);
        var text = dark
            ? Mix(Parse("#f6f8ff"), accentAlt, 0.08)
            : Parse("#111827");
        var muted = dark
            ? Mix(Parse("#b8c1d6"), accentBright, 0.24)
            : Mix(Parse("#4b5563"), accentDeep, 0.12);

        return new DesktopAssistantTheme(
            base950,
            base900,
            panel,
            panelStrong,
            panelSoft,
            dark ? Mix(Parse("#0a101a"), accentDeep, 0.24) : Mix(Parse("#ffffff"), accentAlt, 0.05),
            dark ? Mix(accentBright, Parse("#1d2533"), 0.64) : Mix(accentDeep, Parse("#d7deea"), 0.66),
            text,
            muted,
            accent,
            accentBright,
            accentDeep,
            BestTextOn(Mix(accent, accentBright, 0.48)),
            dark ? Mix(Parse("#ffd36a"), accentAlt, 0.05) : Parse("#925f00"),
            dark ? Mix(Parse("#ff7a94"), accent, 0.10) : Parse("#a92942"),
            dark ? Mix(Parse("#101a29"), accentBright, 0.32) : Mix(Parse("#ffffff"), accentAlt, 0.08),
            dark ? Mix(Parse("#0f1724"), accent, 0.42) : Mix(Parse("#ffffff"), accent, 0.22),
            dark ? Mix(Parse("#33270d"), accent, 0.12) : Mix(Parse("#fff5d6"), accent, 0.08));
    }

    private static string ResolveScheme(string? mode, string? scheme)
    {
        var normalizedScheme = (scheme ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedScheme is "dark" or "light")
        {
            return normalizedScheme;
        }

        var normalizedMode = (mode ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedMode is "dark" or "light")
        {
            return normalizedMode;
        }

        var hour = DateTimeOffset.Now.Hour;
        return hour >= 7 && hour < 19 ? "light" : "dark";
    }

    private static Color BestTextOn(Color background) =>
        ContrastRatio(background, Parse("#ffffff")) >= ContrastRatio(background, Parse("#101828"))
            ? Parse("#ffffff")
            : Parse("#101828");

    private static double ContrastRatio(Color a, Color b)
    {
        var lighter = Math.Max(RelativeLuminance(a), RelativeLuminance(b));
        var darker = Math.Min(RelativeLuminance(a), RelativeLuminance(b));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Convert(byte channel)
        {
            var normalized = channel / 255d;
            return normalized <= 0.03928
                ? normalized / 12.92
                : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        return Convert(color.R) * 0.2126 + Convert(color.G) * 0.7152 + Convert(color.B) * 0.0722;
    }

    private static Color Mix(Color a, Color b, double weightB)
    {
        var weightA = 1 - weightB;
        return Color.FromRgb(
            ClampChannel(a.R * weightA + b.R * weightB),
            ClampChannel(a.G * weightA + b.G * weightB),
            ClampChannel(a.B * weightA + b.B * weightB));
    }

    private static byte ClampChannel(double value) =>
        (byte)Math.Clamp(Math.Round(value), 0, 255);

    private static Color Parse(string hex) => Color.Parse(hex);

    private sealed record Palette(
        string Id,
        string Accent,
        string AccentAlt,
        bool SmoothGrey = false);
}
