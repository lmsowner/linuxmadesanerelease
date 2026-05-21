// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Collections;

namespace LinuxMadeSane.Core.Models.DesktopSession;

public sealed class DesktopSessionEnvironmentDetector
{
    public static readonly IReadOnlyList<string> DiagnosticTools =
    [
        "dbus-send",
        "gdbus",
        "gsettings",
        "glxinfo",
        "loginctl",
        "nvidia-smi",
        "setxkbmap",
        "vainfo",
        "wayland-info",
        "xauth",
        "xdg-open",
        "xdpyinfo",
        "xfconf-query",
        "xrandr",
        "xset"
    ];

    public DesktopSessionCapabilityReport DetectCurrent() =>
        Detect(new DesktopSessionEnvironmentInput(
            ReadEnvironment(),
            CommandExistsOnPath,
            Environment.UserName,
            TryParseInt(Environment.GetEnvironmentVariable("UID")),
            Environment.MachineName,
            Environment.ProcessId,
            DateTimeOffset.UtcNow,
            File.Exists));

    public DesktopSessionCapabilityReport Detect(DesktopSessionEnvironmentInput input)
    {
        var display = Read(input.EnvironmentVariables, "DISPLAY");
        var waylandDisplay = Read(input.EnvironmentVariables, "WAYLAND_DISPLAY");
        var runtimeDirectory = Read(input.EnvironmentVariables, "XDG_RUNTIME_DIR");
        var sessionBus = Read(input.EnvironmentVariables, "DBUS_SESSION_BUS_ADDRESS");
        var hasX11 = !string.IsNullOrWhiteSpace(display);
        var hasWayland = !string.IsNullOrWhiteSpace(waylandDisplay);
        var hasDisplay = hasX11 || hasWayland;
        var hasSessionBus = !string.IsNullOrWhiteSpace(sessionBus) ||
                            (!string.IsNullOrWhiteSpace(runtimeDirectory) &&
                             input.FileExists(Path.Combine(runtimeDirectory, "bus")));
        var availableTools = DiagnosticTools
            .Where(input.CommandExists)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var missingTools = DiagnosticTools
            .Except(availableTools, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new DesktopSessionCapabilityReport(
            string.IsNullOrWhiteSpace(input.UserName) ? "unknown" : input.UserName,
            input.UserId,
            string.IsNullOrWhiteSpace(input.MachineName) ? "unknown" : input.MachineName,
            input.ProcessId,
            Normalize(Read(input.EnvironmentVariables, "XDG_SESSION_ID")),
            DetermineDisplayServer(hasX11, hasWayland),
            Normalize(display),
            Normalize(waylandDisplay),
            Normalize(runtimeDirectory),
            Normalize(Read(input.EnvironmentVariables, "DESKTOP_SESSION")),
            Normalize(Read(input.EnvironmentVariables, "XDG_CURRENT_DESKTOP")),
            Normalize(Read(input.EnvironmentVariables, "XDG_SESSION_TYPE")),
            Normalize(Read(input.EnvironmentVariables, "XDG_SESSION_CLASS")),
            Normalize(Read(input.EnvironmentVariables, "XDG_SESSION_DESKTOP")),
            hasDisplay,
            hasSessionBus,
            hasDisplay && hasSessionBus,
            availableTools,
            missingTools,
            BuildWarnings(hasDisplay, hasSessionBus, runtimeDirectory, input.UserName, missingTools),
            input.CapturedAtUtc);
    }

    private static IReadOnlyDictionary<string, string?> ReadEnvironment()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                values[key] = entry.Value?.ToString();
            }
        }

        return values;
    }

    private static string? Read(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static DesktopSessionDisplayServer DetermineDisplayServer(bool hasX11, bool hasWayland) =>
        (hasX11, hasWayland) switch
        {
            (true, true) => DesktopSessionDisplayServer.X11AndWayland,
            (true, false) => DesktopSessionDisplayServer.X11,
            (false, true) => DesktopSessionDisplayServer.Wayland,
            _ => DesktopSessionDisplayServer.Headless
        };

    private static IReadOnlyList<string> BuildWarnings(
        bool hasDisplay,
        bool hasSessionBus,
        string? runtimeDirectory,
        string userName,
        IReadOnlyList<string> missingTools)
    {
        var warnings = new List<string>();
        if (!hasDisplay)
        {
            warnings.Add("No DISPLAY or WAYLAND_DISPLAY was available in this user session.");
        }

        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            warnings.Add("XDG_RUNTIME_DIR was not set, so user-session services may not be reachable.");
        }

        if (!hasSessionBus)
        {
            warnings.Add("No session D-Bus address was available, so desktop settings and notifications may fail.");
        }

        if (string.Equals(userName, "root", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("The helper is running as root; it should run as the signed-in desktop user.");
        }

        if (missingTools.Count > 0)
        {
            warnings.Add($"Missing optional GUI diagnostic tools: {string.Join(", ", missingTools)}.");
        }

        return warnings;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? TryParseInt(string? value) =>
        int.TryParse(value, out var parsed) ? parsed : null;

    private static bool CommandExistsOnPath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(command);
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed record DesktopSessionEnvironmentInput(
    IReadOnlyDictionary<string, string?> EnvironmentVariables,
    Func<string, bool> CommandExists,
    string UserName,
    int? UserId,
    string MachineName,
    int ProcessId,
    DateTimeOffset CapturedAtUtc,
    Func<string, bool> FileExists);
