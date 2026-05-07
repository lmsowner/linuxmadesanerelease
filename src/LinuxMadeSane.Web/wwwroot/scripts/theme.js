window.lmsTheme = (() => {
    const storageKeys = {
        palette: "lms.theme.palette",
        mode: "lms.theme.mode",
        fontScale: "lms.theme.font-scale"
    };

    const defaultPaletteId = "smooth-grey";
    const defaultMode = "auto";
    const defaultFontScalePercent = 100;
    const minimumFontScalePercent = 90;
    const maximumFontScalePercent = 180;

    const paletteCatalog = [
        { id: "amethyst-haze", name: "Amethyst Haze", accent: "#a96cff", accentAlt: "#ff8bd7" },
        { id: "arctic-mint", name: "Arctic Mint", accent: "#49dcb1", accentAlt: "#8ff7ff" },
        { id: "aurora-berry", name: "Aurora Berry", accent: "#7a5cff", accentAlt: "#4ce5b1" },
        { id: "cherry-glass", name: "Cherry Glass", accent: "#ff4d6d", accentAlt: "#ff9e7a" },
        { id: "citron-ice", name: "Citron Ice", accent: "#b7d94c", accentAlt: "#62e7ff" },
        { id: "cobalt-frost", name: "Cobalt Frost", accent: "#2f5fff", accentAlt: "#a8ddff" },
        { id: "copper-cloud", name: "Copper Cloud", accent: "#c98352", accentAlt: "#ffd27a" },
        { id: "coral-fizz", name: "Coral Fizz", accent: "#ff7f6a", accentAlt: "#ffd36f" },
        { id: "crimson-theatre", name: "Crimson Theatre", accent: "#ff5b8a", accentAlt: "#ff7d5a" },
        { id: "ember-signal", name: "Ember Signal", accent: "#ff7b54", accentAlt: "#ffb36b" },
        { id: "emerald-echo", name: "Emerald Echo", accent: "#20c997", accentAlt: "#96f56f" },
        { id: "forest-static", name: "Forest Static", accent: "#4f9f3d", accentAlt: "#b4ef72" },
        { id: "glacier-cyan", name: "Glacier Cyan", accent: "#3dc7ff", accentAlt: "#9ff3ff" },
        { id: "jade-pulse", name: "Jade Pulse", accent: "#0fbf7f", accentAlt: "#8cffc4" },
        { id: "lime-vault", name: "Lime Vault", accent: "#8fd14f", accentAlt: "#dcff78" },
        { id: "midnight-plum", name: "Midnight Plum", accent: "#8b5cf6", accentAlt: "#f472b6" },
        { id: "molten-sunset", name: "Molten Sunset", accent: "#ff5c7c", accentAlt: "#ffae42" },
        { id: "monsoon-teal", name: "Monsoon Teal", accent: "#1ea7a1", accentAlt: "#6fe6d8" },
        { id: "moon-silver", name: "Moon Silver", accent: "#8b9bb4", accentAlt: "#dbe6ff" },
        { id: "neon-orchid", name: "Neon Orchid", accent: "#d867ff", accentAlt: "#73f3ff" },
        { id: "obsidian-rose", name: "Obsidian Rose", accent: "#7f4d66", accentAlt: "#ff87b5" },
        { id: "ocean-current", name: "Ocean Current", accent: "#2e7bff", accentAlt: "#5be7ff" },
        { id: "peach-voltage", name: "Peach Voltage", accent: "#ff9a5c", accentAlt: "#ff6fcf" },
        { id: "polar-lilac", name: "Polar Lilac", accent: "#c1a6ff", accentAlt: "#8fe7ff" },
        { id: "rose-circuit", name: "Rose Circuit", accent: "#ff78b3", accentAlt: "#b46cff" },
        { id: "ruby-noir", name: "Ruby Noir", accent: "#e54868", accentAlt: "#8d4fff" },
        { id: "sandstorm-gold", name: "Sandstorm Gold", accent: "#d9a441", accentAlt: "#fff29a" },
        { id: "smooth-grey", name: "Smooth Grey", accent: "#0a84ff", accentAlt: "#8e8e93", style: "smooth-grey" },
        { id: "solar-flare", name: "Solar Flare", accent: "#ffb100", accentAlt: "#ff5f45" },
        { id: "storm-indigo", name: "Storm Indigo", accent: "#536dfe", accentAlt: "#7dc3ff" },
        { id: "violet-arc", name: "Violet Arc", accent: "#7d3cff", accentAlt: "#c77dff" }
    ];

    let autoRefreshHandle = null;
    let enhancedThemeHooked = false;
    let nextObserverId = 1;
    const themeObservers = new Map();

    const paletteSelectSelector = "[data-theme-palette-select]";
    const fontScaleSelectSelector = "[data-theme-font-scale-select]";
    const modeSelectSelector = "[data-theme-mode-select]";
    const modeToggleSelector = "[data-theme-mode-toggle]";

    function clampChannel(value) {
        return Math.min(255, Math.max(0, Math.round(value)));
    }

    function normalizeHex(hex) {
        const trimmed = (hex ?? "").trim().replace("#", "");
        if (trimmed.length === 3) {
            return `#${trimmed[0]}${trimmed[0]}${trimmed[1]}${trimmed[1]}${trimmed[2]}${trimmed[2]}`;
        }

        return `#${trimmed.padStart(6, "0").slice(0, 6)}`;
    }

    function hexToRgb(hex) {
        const normalized = normalizeHex(hex);
        return {
            r: Number.parseInt(normalized.slice(1, 3), 16),
            g: Number.parseInt(normalized.slice(3, 5), 16),
            b: Number.parseInt(normalized.slice(5, 7), 16)
        };
    }

    function rgbToHex({ r, g, b }) {
        return `#${clampChannel(r).toString(16).padStart(2, "0")}${clampChannel(g).toString(16).padStart(2, "0")}${clampChannel(b).toString(16).padStart(2, "0")}`;
    }

    function mix(hexA, hexB, weightB) {
        const a = hexToRgb(hexA);
        const b = hexToRgb(hexB);
        const weightA = 1 - weightB;

        return rgbToHex({
            r: a.r * weightA + b.r * weightB,
            g: a.g * weightA + b.g * weightB,
            b: a.b * weightA + b.b * weightB
        });
    }

    function alpha(hex, opacity) {
        const { r, g, b } = hexToRgb(hex);
        return `rgba(${r}, ${g}, ${b}, ${opacity})`;
    }

    function toRgbString(hex) {
        const { r, g, b } = hexToRgb(hex);
        return `${r}, ${g}, ${b}`;
    }

    function relativeLuminance(hex) {
        const { r, g, b } = hexToRgb(hex);
        const channels = [r, g, b].map(value => {
            const normalized = value / 255;
            return normalized <= 0.03928
                ? normalized / 12.92
                : ((normalized + 0.055) / 1.055) ** 2.4;
        });

        return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
    }

    function contrastRatio(hexA, hexB) {
        const luminanceA = relativeLuminance(hexA);
        const luminanceB = relativeLuminance(hexB);
        const lighter = Math.max(luminanceA, luminanceB);
        const darker = Math.min(luminanceA, luminanceB);

        return (lighter + 0.05) / (darker + 0.05);
    }

    function bestTextOn(backgroundHex) {
        const darkText = "#101828";
        const lightText = "#ffffff";

        return contrastRatio(backgroundHex, lightText) >= contrastRatio(backgroundHex, darkText)
            ? lightText
            : darkText;
    }

    function ensureContrast(candidateHex, backgroundHex, fallbackHex = bestTextOn(backgroundHex), minimumRatio = 4.5) {
        if (contrastRatio(candidateHex, backgroundHex) >= minimumRatio) {
            return candidateHex;
        }

        return contrastRatio(fallbackHex, backgroundHex) >= minimumRatio
            ? fallbackHex
            : bestTextOn(backgroundHex);
    }

    function readStoredValue(key, fallbackValue) {
        try {
            return window.localStorage.getItem(key) || fallbackValue;
        } catch {
            return fallbackValue;
        }
    }

    function writeStoredValue(key, value) {
        try {
            window.localStorage.setItem(key, value);
        } catch {
        }
    }

    function normalizeMode(mode) {
        return mode === "light" || mode === "dark" || mode === "auto"
            ? mode
            : defaultMode;
    }

    function normalizeFontScalePercent(value) {
        const numericValue = Number.parseInt(String(value ?? defaultFontScalePercent), 10);
        if (!Number.isFinite(numericValue)) {
            return defaultFontScalePercent;
        }

        return Math.min(maximumFontScalePercent, Math.max(minimumFontScalePercent, numericValue));
    }

    function getPalette(paletteId) {
        return paletteCatalog.find(item => item.id === paletteId) || paletteCatalog.find(item => item.id === defaultPaletteId) || paletteCatalog[0];
    }

    function resolveScheme(mode, now = new Date()) {
        if (mode === "dark" || mode === "light") {
            return mode;
        }

        const currentHour = now.getHours();
        return currentHour >= 7 && currentHour < 19 ? "light" : "dark";
    }

    function buildThemeVariables(palette, scheme) {
        if (palette.style === "smooth-grey") {
            return buildSmoothGreyThemeVariables(palette, scheme);
        }

        const accent = palette.accent;
        const accentAlt = palette.accentAlt;
        const accentCore = mix(accent, accentAlt, 0.18);
        const accentDeep = scheme === "dark"
            ? mix("#03040a", accentCore, 0.82)
            : mix("#1a2233", accentCore, 0.72);
        const accentBright = scheme === "dark"
            ? mix("#ffffff", accentAlt, 0.34)
            : ensureContrast(mix("#1f2937", accentAlt, 0.42), "#ffffff", mix("#111827", accentDeep, 0.22));
        const accentShadow = scheme === "dark"
            ? mix(accentDeep, accentAlt, 0.14)
            : mix(accentDeep, accent, 0.18);

        const base950 = scheme === "dark"
            ? mix("#020308", accentDeep, 0.66)
            : mix("#f4f6fb", accentAlt, 0.12);
        const base900 = scheme === "dark"
            ? mix("#070d17", accent, 0.52)
            : mix("#ffffff", accentAlt, 0.08);
        const base850 = scheme === "dark"
            ? mix("#0d1522", accentShadow, 0.46)
            : mix("#edf2f8", accent, 0.08);
        const base800 = scheme === "dark"
            ? mix("#152132", accentAlt, 0.4)
            : mix("#e3e9f3", accentShadow, 0.10);

        const text = scheme === "dark"
            ? mix("#f6f8ff", accentAlt, 0.08)
            : ensureContrast(mix("#111827", accentDeep, 0.04), base900, "#111827");
        const textMuted = scheme === "dark"
            ? mix("#b8c1d6", accentBright, 0.24)
            : ensureContrast(mix("#4b5563", accentDeep, 0.12), base900, "#4b5563");
        const codeColor = scheme === "dark"
            ? mix("#f4f0ff", accentBright, 0.30)
            : ensureContrast(mix("#27324a", accentDeep, 0.20), base900, "#27324a");
        const onAccent = bestTextOn(mix(accent, accentBright, 0.48));

        const panel = alpha(
            scheme === "dark"
                ? mix("#0c1220", accentDeep, 0.52)
                : mix("#ffffff", accentAlt, 0.08),
            scheme === "dark" ? 0.92 : 0.94);
        const panelStrong = alpha(
            scheme === "dark"
                ? mix("#080d18", accentDeep, 0.62)
                : mix("#ffffff", accent, 0.06),
            scheme === "dark" ? 0.98 : 0.985);
        const panelSoft = alpha(
            scheme === "dark"
                ? mix("#101726", accentAlt, 0.38)
                : mix("#ffffff", accentAlt, 0.08),
            scheme === "dark" ? 0.38 : 0.86);

        const line = alpha(scheme === "dark" ? accentBright : accentDeep, scheme === "dark" ? 0.36 : 0.34);
        const lineStrong = alpha(scheme === "dark" ? accentBright : accentDeep, scheme === "dark" ? 0.58 : 0.56);

        const fieldBackground = alpha(
            scheme === "dark"
                ? mix("#0a101a", accentDeep, 0.24)
                : mix("#ffffff", accentAlt, 0.05),
            scheme === "dark" ? 0.92 : 0.96);
        const fieldBackgroundStrong = alpha(
            scheme === "dark"
                ? mix("#070c15", accent, 0.18)
                : mix("#ffffff", accentAlt, 0.04),
            scheme === "dark" ? 0.98 : 0.98);
        const surfaceSoft1 = alpha(
            scheme === "dark"
                ? mix("#0d1420", accentBright, 0.26)
                : mix("#ffffff", accentAlt, 0.06),
            scheme === "dark" ? 0.22 : 0.76);
        const surfaceSoft2 = alpha(
            scheme === "dark"
                ? mix("#101a29", accentBright, 0.32)
                : mix("#ffffff", accentAlt, 0.08),
            scheme === "dark" ? 0.3 : 0.86);
        const surfaceSoft3 = alpha(
            scheme === "dark"
                ? mix("#132031", accentBright, 0.4)
                : mix("#ffffff", accentAlt, 0.10),
            scheme === "dark" ? 0.42 : 0.92);
        const surfaceElevated = alpha(
            scheme === "dark"
                ? mix("#090f1a", accentDeep, 0.5)
                : mix("#ffffff", accentAlt, 0.06),
            scheme === "dark" ? 0.98 : 0.98);
        const surfaceTerminal = alpha(
            scheme === "dark"
                ? mix("#060c15", accentDeep, 0.44)
                : mix("#ffffff", accentAlt, 0.04),
            scheme === "dark" ? 0.96 : 0.95);
        const surfaceCode = alpha(
            scheme === "dark"
                ? mix("#09101b", accentDeep, 0.34)
                : mix("#ffffff", accentAlt, 0.04),
            scheme === "dark" ? 0.94 : 0.94);

        const success = scheme === "dark"
            ? mix("#7bdcb5", accentAlt, 0.08)
            : ensureContrast(mix("#177245", accentAlt, 0.08), base900, "#17643e");
        const warning = scheme === "dark"
            ? mix("#ffd36a", accentAlt, 0.05)
            : ensureContrast(mix("#925f00", accentAlt, 0.06), base900, "#7a4d00");
        const danger = scheme === "dark"
            ? mix("#ff7a94", accent, 0.10)
            : ensureContrast(mix("#a92942", accent, 0.08), base900, "#8f1f34");

        const shadowSoft = scheme === "dark"
            ? `0 30px 80px ${alpha("#02040a", 0.62)}, 0 0 72px ${alpha(accentDeep, 0.44)}`
            : `0 24px 56px ${alpha("#c9d5ef", 0.44)}, 0 0 54px ${alpha(accent, 0.26)}`;
        const shadowCard = scheme === "dark"
            ? `0 20px 48px ${alpha("#040710", 0.42)}, 0 0 40px ${alpha(accent, 0.3)}`
            : `0 18px 40px ${alpha("#c7d2ea", 0.36)}, 0 0 34px ${alpha(accent, 0.22)}`;
        const shadowFocus = `0 0 0 3px ${alpha(accent, scheme === "dark" ? 0.28 : 0.2)}`;
        const surfaceGlass = `linear-gradient(180deg, ${alpha("#ffffff", scheme === "dark" ? 0.12 : 0.52)}, ${alpha("#ffffff", scheme === "dark" ? 0.025 : 0.2)}), linear-gradient(135deg, ${alpha(accent, scheme === "dark" ? 0.4 : 0.28)}, ${alpha(accentAlt, scheme === "dark" ? 0.26 : 0.22)}), ${panel}`;
        const successSoft = alpha(success, scheme === "dark" ? 0.14 : 0.12);
        const successLine = alpha(success, scheme === "dark" ? 0.24 : 0.22);
        const warningSoft = alpha(warning, scheme === "dark" ? 0.14 : 0.12);
        const warningLine = alpha(warning, scheme === "dark" ? 0.26 : 0.24);
        const dangerSoft = alpha(danger, scheme === "dark" ? 0.14 : 0.12);
        const dangerLine = alpha(danger, scheme === "dark" ? 0.26 : 0.24);
        const neutralText = scheme === "dark"
            ? accentBright
            : ensureContrast(mix("#2f3b52", accentDeep, 0.18), base900, "#2f3b52");
        const neutralSoft = alpha(neutralText, scheme === "dark" ? 0.12 : 0.12);
        const neutralLine = alpha(neutralText, scheme === "dark" ? 0.22 : 0.24);

        return {
            "--color-base-950": base950,
            "--color-base-900": base900,
            "--color-base-850": base850,
            "--color-base-800": base800,
            "--color-panel": panel,
            "--color-panel-strong": panelStrong,
            "--color-panel-soft": panelSoft,
            "--color-line": line,
            "--color-line-strong": lineStrong,
            "--color-text": text,
            "--color-text-muted": textMuted,
            "--color-text-secondary": textMuted,
            "--color-code": codeColor,
            "--color-on-accent": onAccent,
            "--color-accent": accent,
            "--color-accent-rgb": toRgbString(accent),
            "--color-accent-bright": accentBright,
            "--color-accent-bright-rgb": toRgbString(accentBright),
            "--color-accent-deep": accentDeep,
            "--color-accent-deep-rgb": toRgbString(accentDeep),
            "--color-success": success,
            "--color-warning": warning,
            "--color-danger": danger,
            "--field-background": fieldBackground,
            "--field-background-strong": fieldBackgroundStrong,
            "--surface-soft-1": surfaceSoft1,
            "--surface-soft-2": surfaceSoft2,
            "--surface-soft-3": surfaceSoft3,
            "--surface-elevated": surfaceElevated,
            "--surface-terminal": surfaceTerminal,
            "--surface-code": surfaceCode,
            "--color-success-soft": successSoft,
            "--color-success-line": successLine,
            "--color-warning-soft": warningSoft,
            "--color-warning-line": warningLine,
            "--color-danger-soft": dangerSoft,
            "--color-danger-line": dangerLine,
            "--color-neutral-soft": neutralSoft,
            "--color-neutral-line": neutralLine,
            "--color-success-text": success,
            "--color-warning-text": warning,
            "--color-danger-text": danger,
            "--color-neutral-text": neutralText,
            "--shadow-soft": shadowSoft,
            "--shadow-card": shadowCard,
            "--shadow-focus": shadowFocus,
            "--surface-glass": surfaceGlass,
            "--app-glow-a": alpha(accent, scheme === "dark" ? 0.56 : 0.34),
            "--app-glow-b": alpha(accentAlt, scheme === "dark" ? 0.4 : 0.28),
            "--app-glow-c": alpha(accentDeep, scheme === "dark" ? 0.36 : 0.2)
        };
    }

    function buildSmoothGreyThemeVariables(palette, scheme) {
        const accent = scheme === "dark" ? "#0a84ff" : "#007aff";
        const accentAlt = scheme === "dark" ? "#8e8e93" : "#8e8e93";
        const accentBright = scheme === "dark" ? "#5ac8fa" : "#409cff";
        const accentDeep = scheme === "dark" ? "#355c8a" : "#3a5f94";

        const base950 = scheme === "dark" ? "#111214" : "#eef0f3";
        const base900 = scheme === "dark" ? "#1c1c1e" : "#f5f5f7";
        const base850 = scheme === "dark" ? "#242426" : "#ebedf0";
        const base800 = scheme === "dark" ? "#2c2c2e" : "#e1e3e8";

        const panel = scheme === "dark" ? "rgba(30, 30, 32, 0.88)" : "rgba(255, 255, 255, 0.84)";
        const panelStrong = scheme === "dark" ? "rgba(36, 36, 38, 0.96)" : "rgba(255, 255, 255, 0.94)";
        const panelSoft = scheme === "dark" ? "rgba(255, 255, 255, 0.05)" : "rgba(255, 255, 255, 0.62)";

        const text = scheme === "dark" ? "#f5f5f7" : "#1d1d1f";
        const textMuted = scheme === "dark" ? "#aeaeb2" : "#6e6e73";
        const codeColor = scheme === "dark" ? "#d7ebff" : "#25466f";
        const onAccent = scheme === "dark" ? "#081018" : "#ffffff";

        const line = scheme === "dark" ? "rgba(120, 120, 128, 0.34)" : "rgba(60, 60, 67, 0.16)";
        const lineStrong = scheme === "dark" ? "rgba(120, 120, 128, 0.48)" : "rgba(60, 60, 67, 0.26)";

        const fieldBackground = scheme === "dark" ? "rgba(44, 44, 46, 0.9)" : "rgba(255, 255, 255, 0.88)";
        const fieldBackgroundStrong = scheme === "dark" ? "rgba(54, 54, 56, 0.96)" : "rgba(255, 255, 255, 0.96)";
        const surfaceSoft1 = scheme === "dark" ? "rgba(255, 255, 255, 0.024)" : "rgba(255, 255, 255, 0.56)";
        const surfaceSoft2 = scheme === "dark" ? "rgba(255, 255, 255, 0.042)" : "rgba(255, 255, 255, 0.74)";
        const surfaceSoft3 = scheme === "dark" ? "rgba(255, 255, 255, 0.068)" : "rgba(255, 255, 255, 0.9)";
        const surfaceElevated = scheme === "dark" ? "rgba(44, 44, 46, 0.98)" : "rgba(255, 255, 255, 0.98)";
        const surfaceTerminal = scheme === "dark" ? "rgba(24, 24, 26, 0.96)" : "rgba(250, 250, 252, 0.95)";
        const surfaceCode = scheme === "dark" ? "rgba(32, 35, 40, 0.94)" : "rgba(248, 249, 251, 0.95)";

        const success = scheme === "dark" ? "#32d74b" : "#248a3d";
        const warning = scheme === "dark" ? "#ffd60a" : "#b78103";
        const danger = scheme === "dark" ? "#ff453a" : "#d92c20";

        const shadowSoft = scheme === "dark"
            ? "0 26px 60px rgba(0, 0, 0, 0.42), 0 0 32px rgba(10, 132, 255, 0.1)"
            : "0 20px 46px rgba(118, 124, 138, 0.22), 0 0 28px rgba(0, 122, 255, 0.08)";
        const shadowCard = scheme === "dark"
            ? "0 16px 32px rgba(0, 0, 0, 0.32), 0 0 18px rgba(10, 132, 255, 0.08)"
            : "0 14px 28px rgba(118, 124, 138, 0.18), 0 0 18px rgba(0, 122, 255, 0.06)";
        const shadowFocus = scheme === "dark"
            ? "0 0 0 3px rgba(10, 132, 255, 0.28)"
            : "0 0 0 3px rgba(0, 122, 255, 0.2)";
        const surfaceGlass = scheme === "dark"
            ? "linear-gradient(180deg, rgba(255, 255, 255, 0.08), rgba(255, 255, 255, 0.02)), linear-gradient(180deg, rgba(58, 58, 60, 0.82), rgba(28, 28, 30, 0.9)), rgba(30, 30, 32, 0.88)"
            : "linear-gradient(180deg, rgba(255, 255, 255, 0.9), rgba(255, 255, 255, 0.68)), linear-gradient(180deg, rgba(232, 234, 238, 0.74), rgba(245, 245, 247, 0.84)), rgba(255, 255, 255, 0.84)";

        return {
            "--color-base-950": base950,
            "--color-base-900": base900,
            "--color-base-850": base850,
            "--color-base-800": base800,
            "--color-panel": panel,
            "--color-panel-strong": panelStrong,
            "--color-panel-soft": panelSoft,
            "--color-line": line,
            "--color-line-strong": lineStrong,
            "--color-text": text,
            "--color-text-muted": textMuted,
            "--color-text-secondary": textMuted,
            "--color-code": codeColor,
            "--color-on-accent": onAccent,
            "--color-accent": accent,
            "--color-accent-rgb": toRgbString(accent),
            "--color-accent-bright": accentBright,
            "--color-accent-bright-rgb": toRgbString(accentBright),
            "--color-accent-deep": accentDeep,
            "--color-accent-deep-rgb": toRgbString(accentDeep),
            "--color-success": success,
            "--color-warning": warning,
            "--color-danger": danger,
            "--field-background": fieldBackground,
            "--field-background-strong": fieldBackgroundStrong,
            "--surface-soft-1": surfaceSoft1,
            "--surface-soft-2": surfaceSoft2,
            "--surface-soft-3": surfaceSoft3,
            "--surface-elevated": surfaceElevated,
            "--surface-terminal": surfaceTerminal,
            "--surface-code": surfaceCode,
            "--color-success-soft": alpha(success, scheme === "dark" ? 0.14 : 0.12),
            "--color-success-line": alpha(success, scheme === "dark" ? 0.22 : 0.2),
            "--color-warning-soft": alpha(warning, scheme === "dark" ? 0.14 : 0.12),
            "--color-warning-line": alpha(warning, scheme === "dark" ? 0.24 : 0.22),
            "--color-danger-soft": alpha(danger, scheme === "dark" ? 0.14 : 0.12),
            "--color-danger-line": alpha(danger, scheme === "dark" ? 0.24 : 0.22),
            "--color-neutral-soft": scheme === "dark" ? "rgba(174, 174, 178, 0.12)" : "rgba(110, 110, 115, 0.12)",
            "--color-neutral-line": scheme === "dark" ? "rgba(174, 174, 178, 0.2)" : "rgba(110, 110, 115, 0.18)",
            "--color-success-text": success,
            "--color-warning-text": warning,
            "--color-danger-text": danger,
            "--color-neutral-text": accentAlt,
            "--shadow-soft": shadowSoft,
            "--shadow-card": shadowCard,
            "--shadow-focus": shadowFocus,
            "--surface-glass": surfaceGlass,
            "--app-glow-a": scheme === "dark" ? "rgba(10, 132, 255, 0.1)" : "rgba(255, 255, 255, 0.34)",
            "--app-glow-b": scheme === "dark" ? "rgba(142, 142, 147, 0.08)" : "rgba(210, 214, 221, 0.28)",
            "--app-glow-c": scheme === "dark" ? "rgba(118, 118, 128, 0.14)" : "rgba(188, 193, 201, 0.22)"
        };
    }

    function dispatchThemeChange(detail) {
        window.dispatchEvent(new CustomEvent("lms-theme-change", { detail }));

        themeObservers.forEach(dotNetReference => {
            try {
                Promise.resolve(dotNetReference.invokeMethodAsync("HandleThemeChanged", detail)).catch(() => {});
            } catch {
            }
        });
    }

    function stopAutoRefresh() {
        if (autoRefreshHandle !== null) {
            window.clearInterval(autoRefreshHandle);
            autoRefreshHandle = null;
        }
    }

    function startAutoRefresh() {
        stopAutoRefresh();
        autoRefreshHandle = window.setInterval(() => {
            const settings = getThemeSettings();
            if (settings.mode === "auto") {
                applyTheme(settings.paletteId, settings.mode);
            }
        }, 60_000);
    }

    function applyFontScale(fontScalePercent) {
        const root = document.documentElement;
        const normalizedFontScalePercent = normalizeFontScalePercent(fontScalePercent);

        root.style.setProperty("--app-font-scale", `${normalizedFontScalePercent / 100}`);
        root.dataset.themeFontScale = String(normalizedFontScalePercent);

        return normalizedFontScalePercent;
    }

    function applyTheme(paletteId, mode, fontScalePercent = defaultFontScalePercent) {
        const palette = getPalette(paletteId);
        const normalizedMode = normalizeMode(mode);
        const normalizedFontScalePercent = applyFontScale(fontScalePercent);
        const scheme = resolveScheme(normalizedMode);
        const variables = buildThemeVariables(palette, scheme);
        const root = document.documentElement;

        Object.entries(variables).forEach(([name, value]) => {
            root.style.setProperty(name, value);
        });

        root.dataset.themePalette = palette.id;
        root.dataset.themeStyle = palette.style || "default";
        root.dataset.themeMode = normalizedMode;
        root.dataset.themeScheme = scheme;
        root.style.colorScheme = scheme;

        if (normalizedMode === "auto") {
            startAutoRefresh();
        } else {
            stopAutoRefresh();
        }

        const settings = {
            paletteId: palette.id,
            paletteName: palette.name,
            paletteStyle: palette.style || "default",
            mode: normalizedMode,
            scheme,
            fontScalePercent: normalizedFontScalePercent
        };

        dispatchThemeChange(settings);
        return settings;
    }

    function applySavedTheme() {
        const paletteId = readStoredValue(storageKeys.palette, defaultPaletteId);
        const mode = normalizeMode(readStoredValue(storageKeys.mode, defaultMode));
        const fontScalePercent = normalizeFontScalePercent(readStoredValue(storageKeys.fontScale, defaultFontScalePercent));
        return applyTheme(paletteId, mode, fontScalePercent);
    }

    function getThemeSettings() {
        const root = document.documentElement;
        const paletteId = root.dataset.themePalette || readStoredValue(storageKeys.palette, defaultPaletteId);
        const palette = getPalette(paletteId);
        const mode = normalizeMode(root.dataset.themeMode || readStoredValue(storageKeys.mode, defaultMode));
        const scheme = root.dataset.themeScheme || resolveScheme(mode);
        const fontScalePercent = normalizeFontScalePercent(root.dataset.themeFontScale || readStoredValue(storageKeys.fontScale, defaultFontScalePercent));

        return {
            paletteId: palette.id,
            paletteName: palette.name,
            paletteStyle: palette.style || "default",
            mode,
            scheme,
            fontScalePercent
        };
    }

    function setTheme(paletteId, mode, fontScalePercent = defaultFontScalePercent) {
        const palette = getPalette(paletteId);
        const normalizedMode = normalizeMode(mode);
        const normalizedFontScalePercent = normalizeFontScalePercent(fontScalePercent);

        writeStoredValue(storageKeys.palette, palette.id);
        writeStoredValue(storageKeys.mode, normalizedMode);
        writeStoredValue(storageKeys.fontScale, String(normalizedFontScalePercent));

        return applyTheme(palette.id, normalizedMode, normalizedFontScalePercent);
    }

    function setPalette(paletteId) {
        const settings = getThemeSettings();
        return setTheme(paletteId, settings.mode, settings.fontScalePercent);
    }

    function setMode(mode) {
        const settings = getThemeSettings();
        return setTheme(settings.paletteId, mode, settings.fontScalePercent);
    }

    function setFontScale(fontScalePercent) {
        const settings = getThemeSettings();
        return setTheme(settings.paletteId, settings.mode, fontScalePercent);
    }

    function applyUserPreferences(preferences) {
        if (!preferences || typeof preferences !== "object") {
            return applySavedTheme();
        }

        const settings = getThemeSettings();
        return setTheme(
            preferences.paletteId ?? settings.paletteId,
            preferences.mode ?? settings.mode,
            preferences.fontScalePercent ?? settings.fontScalePercent);
    }

    function getPaletteOptions() {
        return paletteCatalog.map(({ id, name }) => ({ id, name }));
    }

    function syncThemeControls() {
        if (typeof document === "undefined") {
            return;
        }

        const settings = getThemeSettings();

        document.querySelectorAll(paletteSelectSelector).forEach(select => {
            if (select.value !== settings.paletteId) {
                select.value = settings.paletteId;
            }
        });

        document.querySelectorAll(fontScaleSelectSelector).forEach(select => {
            const fontScaleValue = String(settings.fontScalePercent);
            if (select.value !== fontScaleValue) {
                select.value = fontScaleValue;
            }
        });

        document.querySelectorAll(modeSelectSelector).forEach(select => {
            if (select.value !== settings.mode) {
                select.value = settings.mode;
            }
        });

        document.querySelectorAll(modeToggleSelector).forEach(toggle => {
            if (!(toggle instanceof HTMLInputElement)) {
                return;
            }

            const shouldBeChecked = settings.scheme === "dark";
            if (toggle.checked !== shouldBeChecked) {
                toggle.checked = shouldBeChecked;
            }
        });
    }

    function handleThemeControlChange(event) {
        const target = event.target;
        if (target instanceof HTMLSelectElement) {
            if (target.matches(paletteSelectSelector)) {
                setPalette(target.value);
                syncThemeControls();
                return;
            }

            if (target.matches(fontScaleSelectSelector)) {
                setFontScale(target.value);
                syncThemeControls();
                return;
            }

            if (target.matches(modeSelectSelector)) {
                setMode(target.value);
                syncThemeControls();
                return;
            }
        }

        if (target instanceof HTMLInputElement && target.matches(modeToggleSelector)) {
            setMode(target.checked ? "dark" : "light");
            syncThemeControls();
        }
    }

    function ensureThemeUiBinding() {
        if (document.documentElement.dataset.themeUiBound !== "1") {
            document.addEventListener("change", handleThemeControlChange, true);
            document.documentElement.dataset.themeUiBound = "1";
        }
    }

    function refreshThemeUi() {
        applySavedTheme();
        syncThemeControls();
    }

    function ensureEnhancedThemeBinding() {
        if (enhancedThemeHooked) {
            return;
        }

        const api = window.blazor || window.Blazor;
        if (!api || typeof api.addEventListener !== "function") {
            window.setTimeout(ensureEnhancedThemeBinding, 100);
            return;
        }

        api.addEventListener("enhancedload", () => {
            refreshThemeUi();
        });

        enhancedThemeHooked = true;
    }

    if (typeof document !== "undefined") {
        refreshThemeUi();
        ensureThemeUiBinding();
        ensureEnhancedThemeBinding();

        document.addEventListener("visibilitychange", () => {
            if (document.visibilityState === "visible" && getThemeSettings().mode === "auto") {
                applySavedTheme();
            }
            syncThemeControls();
        });

        window.addEventListener("focus", () => {
            if (getThemeSettings().mode === "auto") {
                applySavedTheme();
            }
            syncThemeControls();
        });
    }

    return {
        applySavedTheme,
        applyUserPreferences,
        getThemeSettings,
        getPaletteOptions,
        syncThemeControls,
        registerObserver(dotNetReference) {
            const observerId = nextObserverId++;
            themeObservers.set(observerId, dotNetReference);
            return observerId;
        },
        unregisterObserver(observerId) {
            themeObservers.delete(observerId);
        },
        setTheme,
        setPalette,
        setMode,
        setFontScale
    };
})();

window.lmsForms = {
    readInputValues: (...elements) => elements.map((element) => element?.value ?? "")
};

window.lmsLayout = (() => {
    const sidebarKey = "lms.layout.sidebar-collapsed";

    function getSidebarCollapsed() {
        return false;
    }

    function clearSidebarCollapsed() {
        document.documentElement.classList.remove("sidebar-collapsed");
        document.querySelectorAll(".sidebar-collapsed").forEach(element => {
            element.classList.remove("sidebar-collapsed");
        });

        try {
            window.localStorage.removeItem(sidebarKey);
        } catch {
        }
    }

    function setSidebarCollapsed() {
        clearSidebarCollapsed();
    }

    function initializeSidebar() {
        clearSidebarCollapsed();
    }

    clearSidebarCollapsed();

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initializeSidebar, { once: true });
    } else {
        initializeSidebar();
    }

    document.addEventListener("enhancedload", initializeSidebar);
    window.addEventListener("pageshow", initializeSidebar);

    return {
        getSidebarCollapsed,
        setSidebarCollapsed,
        initializeSidebar
    };
})();

window.lmsAbout = (() => {
    const modalSelector = "[data-about-splash]";
    const openSelector = "[data-about-open]";
    const closeSelector = "[data-about-close]";
    let eventsBound = false;
    let previousFocus = null;

    function getModal() {
        return document.querySelector(modalSelector);
    }

    function isOpen() {
        const modal = getModal();
        return !!modal && !modal.hidden;
    }

    function openAbout() {
        const modal = getModal();
        if (!modal) {
            return;
        }

        previousFocus = document.activeElement instanceof HTMLElement
            ? document.activeElement
            : null;

        modal.hidden = false;
        modal.setAttribute("aria-hidden", "false");
        document.documentElement.classList.add("about-splash-open");

        window.requestAnimationFrame(() => {
            const closeButton = modal.querySelector(closeSelector);
            if (closeButton instanceof HTMLElement) {
                closeButton.focus();
            }
        });
    }

    function closeAbout() {
        const modal = getModal();
        if (!modal) {
            return;
        }

        modal.hidden = true;
        modal.setAttribute("aria-hidden", "true");
        document.documentElement.classList.remove("about-splash-open");

        if (previousFocus?.isConnected) {
            previousFocus.focus();
        }

        previousFocus = null;
    }

    function handleDocumentClick(event) {
        const target = event.target instanceof Element ? event.target : null;
        if (!target) {
            return;
        }

        const opener = target.closest(openSelector);
        if (opener) {
            event.preventDefault();
            event.stopPropagation();
            openAbout();
            return;
        }

        const closer = target.closest(closeSelector);
        if (closer) {
            event.preventDefault();
            event.stopPropagation();
            closeAbout();
            return;
        }

        const modal = getModal();
        if (modal && event.target === modal) {
            closeAbout();
        }
    }

    function handleDocumentKeydown(event) {
        if (event.key === "Escape" && isOpen()) {
            event.preventDefault();
            closeAbout();
        }
    }

    function initializeAbout() {
        if (eventsBound) {
            return;
        }

        eventsBound = true;
        document.addEventListener("click", handleDocumentClick, true);
        document.addEventListener("keydown", handleDocumentKeydown, true);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initializeAbout, { once: true });
    } else {
        initializeAbout();
    }

    document.addEventListener("enhancedload", initializeAbout);
    window.addEventListener("pageshow", initializeAbout);

    return {
        openAbout,
        closeAbout
    };
})();

window.lmsSplitPane = (() => {
    function beginResize(container, options, dotNetReference) {
        if (!container || !options || !dotNetReference || !options.callbackMethod) {
            return false;
        }

        const orientation = options.orientation === "bottom" ? "bottom" : "right";
        const pointerX = Number.isFinite(options.pointerX) ? options.pointerX : 0;
        const pointerY = Number.isFinite(options.pointerY) ? options.pointerY : 0;
        const minSize = Math.max(1, Number.isFinite(options.minSize) ? options.minSize : 240);
        const maxSize = Math.max(minSize, Number.isFinite(options.maxSize) ? options.maxSize : 1200);
        const minPrimarySize = Math.max(1, Number.isFinite(options.minPrimarySize) ? options.minPrimarySize : 320);
        const splitterSize = Math.max(0, Number.isFinite(options.splitterSize) ? options.splitterSize : 10);
        const cssVariableName = typeof options.cssVariableName === "string" && options.cssVariableName.length > 0
            ? options.cssVariableName
            : "--split-pane-size";
        const contextId = typeof options.contextId === "string" ? options.contextId : "";
        const bodyResizeClass = orientation === "bottom"
            ? "split-resize-active-y"
            : "split-resize-active-x";
        let latestSize = 0;

        function clampSize(clientX, clientY) {
            const rect = container.getBoundingClientRect();
            const requestedSize = orientation === "bottom"
                ? rect.bottom - clientY
                : rect.right - clientX;
            const totalSize = orientation === "bottom" ? rect.height : rect.width;
            const maximumSize = Math.max(minSize, Math.min(maxSize, totalSize - minPrimarySize - splitterSize));
            return Math.max(minSize, Math.min(maximumSize, requestedSize));
        }

        function applySize(clientX, clientY) {
            latestSize = Math.round(clampSize(clientX, clientY));
            container.style.setProperty(cssVariableName, `${latestSize}px`);
        }

        function handlePointerMove(event) {
            applySize(event.clientX, event.clientY);
        }

        async function handlePointerUp() {
            document.removeEventListener("pointermove", handlePointerMove);
            document.removeEventListener("pointerup", handlePointerUp);
            document.body.classList.remove(bodyResizeClass);

            if (latestSize <= 0) {
                return;
            }

            try {
                await dotNetReference.invokeMethodAsync(options.callbackMethod, contextId, latestSize);
            } catch {
            }
        }

        document.body.classList.add(bodyResizeClass);
        applySize(pointerX, pointerY);
        document.addEventListener("pointermove", handlePointerMove);
        document.addEventListener("pointerup", handlePointerUp, { once: true });
        return true;
    }

    return {
        beginResize
    };
})();

window.lmsModal = (() => {
    function beginDrag(panel, options) {
        if (!panel || !options) {
            return false;
        }

        const pointerX = Number.isFinite(options.pointerX) ? options.pointerX : 0;
        const pointerY = Number.isFinite(options.pointerY) ? options.pointerY : 0;
        const margin = Math.max(0, Number.isFinite(options.margin) ? options.margin : 12);
        const rect = panel.getBoundingClientRect();
        const width = Math.round(rect.width);
        const height = Math.round(rect.height);
        const offsetX = pointerX - rect.left;
        const offsetY = pointerY - rect.top;
        const bodyDragClass = "modal-drag-active";

        function clamp(value, minimum, maximum) {
            return Math.max(minimum, Math.min(maximum, value));
        }

        function applyPosition(clientX, clientY) {
            const maxLeft = Math.max(margin, window.innerWidth - margin - width);
            const maxTop = Math.max(margin, window.innerHeight - margin - height);
            const left = clamp(clientX - offsetX, margin, maxLeft);
            const top = clamp(clientY - offsetY, margin, maxTop);

            panel.style.position = "fixed";
            panel.style.left = `${left}px`;
            panel.style.top = `${top}px`;
            panel.style.width = `${width}px`;
            panel.style.height = `${height}px`;
            panel.style.margin = "0";
        }

        function handlePointerMove(event) {
            applyPosition(event.clientX, event.clientY);
        }

        function handlePointerUp() {
            document.removeEventListener("pointermove", handlePointerMove);
            document.removeEventListener("pointerup", handlePointerUp);
            document.body.classList.remove(bodyDragClass);
        }

        document.body.classList.add(bodyDragClass);
        applyPosition(pointerX, pointerY);
        document.addEventListener("pointermove", handlePointerMove);
        document.addEventListener("pointerup", handlePointerUp, { once: true });
        return true;
    }

    return {
        beginDrag
    };
})();
