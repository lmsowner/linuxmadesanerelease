// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public static class RdpOptimizerCatalog
{
    public static readonly string[] RequiredXrdpPackages =
    [
        "xrdp",
        "xorgxrdp"
    ];

    public static readonly string[] RequiredXfcePackages =
    [
        "xfce4",
        "xfce4-goodies",
        "dbus-x11",
        "lightdm"
    ];

    public static readonly string[] RequiredGnomePackages =
    [
        "ubuntu-desktop-minimal",
        "gdm3"
    ];

    public static readonly string[] RequiredKdePackages =
    [
        "kde-plasma-desktop",
        "sddm"
    ];

    public static readonly string[] SafeRemovableGnomePackages =
    [
        "ubuntu-desktop",
        "ubuntu-desktop-minimal",
        "gnome-shell",
        "gnome-session-bin",
        "gnome-shell-extension-ubuntu-dock",
        "gnome-software",
        "gnome-remote-desktop",
        "evolution",
        "rhythmbox",
        "totem"
    ];

    public static readonly string[] RelevantPackages =
    [
        "xrdp",
        "xorgxrdp",
        "xfce4",
        "xfce4-goodies",
        "dbus-x11",
        "gdm3",
        "lightdm",
        "gnome-shell",
        "gnome-session-bin",
        "gnome-shell-extension-ubuntu-dock",
        "gnome-software",
        "gnome-remote-desktop",
        "kde-plasma-desktop",
        "sddm",
        "tracker-miner-fs-3",
        "tracker-extract-3",
        "tracker-writeback-3",
        "ubuntu-desktop",
        "ubuntu-desktop-minimal",
        "evolution",
        "rhythmbox",
        "totem"
    ];

    public static readonly string[] RelevantServices =
    [
        "xrdp.service",
        "xrdp-sesman.service",
        "gdm.service",
        "gdm3.service",
        "lightdm.service",
        "sddm.service",
        "display-manager.service"
    ];

    public static readonly string[] GnomeAutostartFiles =
    [
        "gnome-software-service.desktop",
        "org.gnome.Evolution-alarm-notify.desktop",
        "update-notifier.desktop",
        "org.gnome.SettingsDaemon.Sharing.desktop",
        "org.gnome.SettingsDaemon.Wacom.desktop"
    ];

    public static readonly string[] SessionFiles =
    [
        "/etc/xrdp/startwm.sh",
        "/etc/skel/.xsession",
        "/etc/X11/default-display-manager"
    ];

    public const string LightdmPath = "/usr/sbin/lightdm";
    public const string GdmPath = "/usr/sbin/gdm3";
    public const string SddmPath = "/usr/bin/sddm";
}
