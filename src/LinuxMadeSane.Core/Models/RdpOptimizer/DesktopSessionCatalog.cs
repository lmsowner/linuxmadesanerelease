// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record DesktopSessionEntry(string Id, string Name, string Command, string Type);

public static class DesktopSessionCatalog
{
    public static bool NeedsDisplayManager(RdpOptimizationProfile profile, string? currentManager)
    {
        if (string.IsNullOrWhiteSpace(currentManager)) return true;
        return false;
    }

    public static DesktopSessionEntry? Find(RdpOptimizationProfile profile, bool remote, string root = "/usr/share")
    {
        var ids = profile switch
        {
            RdpOptimizationProfile.RdpOptimizedXfce => new[] { "xfce" },
            RdpOptimizationProfile.GnomeDesktop => new[] { "ubuntu", "gnome", "gnome-xorg" },
            RdpOptimizationProfile.KdePlasmaDesktop => new[] { "plasmax11", "plasma", "plasmawayland" },
            _ => Array.Empty<string>()
        };
        foreach (var type in remote ? new[] { "x11" } : new[] { "x11", "wayland" })
            foreach (var id in ids)
            {
                var path = Path.Combine(root, type == "x11" ? "xsessions" : "wayland-sessions", id + ".desktop");
                if (!File.Exists(path)) continue;
                var values = File.ReadLines(path).Where(l => l.StartsWith("Exec=") || l.StartsWith("Name=") || l.StartsWith("Hidden="))
                    .Select(l => l.Split('=', 2)).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);
                if (values.GetValueOrDefault("Hidden") == "true" || !values.TryGetValue("Exec", out var command)) continue;
                return new(id, values.GetValueOrDefault("Name", id), command, type);
            }
        return null;
    }

    public static string Marker(RdpOptimizationProfile profile) => profile switch
    {
        RdpOptimizationProfile.RdpOptimizedXfce => "XFCE",
        RdpOptimizationProfile.GnomeDesktop => "GNOME",
        RdpOptimizationProfile.KdePlasmaDesktop => "KDE Plasma",
        _ => "System default"
    };
}
