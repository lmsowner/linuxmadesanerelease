// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Diagnostics;
using System.Text;
using LinuxMadeSane.Core.Models.DesktopSession;

namespace LinuxMadeSane.DesktopHelper;

internal static class DesktopReadOnlyDiagnosticsCollector
{
    private const int CommandTimeoutMilliseconds = 1500;
    private const int MaxOutputChars = 2400;

    private static readonly IReadOnlyList<DiagnosticCommand> Commands =
    [
        new("keyboard.setxkbmap", "setxkbmap", ["-query"]),
        new("keyboard.gsettings.sources", "gsettings", ["get", "org.gnome.desktop.input-sources", "sources"]),
        new("keyboard.gsettings.xkb-options", "gsettings", ["get", "org.gnome.desktop.input-sources", "xkb-options"]),
        new("keyboard.xfconf", "xfconf-query", ["-c", "keyboard-layout", "-lv"]),
        new("display.xrandr", "xrandr", ["--current"]),
        new("display.xrandr-monitors", "xrandr", ["--listmonitors"]),
        new("display.xset", "xset", ["q"]),
        new("display.xdpyinfo", "xdpyinfo", []),
        new("xfce.displays", "xfconf-query", ["-c", "displays", "-lv"]),
        new("xfce.xsettings", "xfconf-query", ["-c", "xsettings", "-lv"]),
        new("xfce.window-manager", "xfconf-query", ["-c", "xfwm4", "-lv"]),
        new("gnome.interface.scaling-factor", "gsettings", ["get", "org.gnome.desktop.interface", "scaling-factor"]),
        new("gnome.interface.text-scaling-factor", "gsettings", ["get", "org.gnome.desktop.interface", "text-scaling-factor"]),
        new("gnome.interface.gtk-theme", "gsettings", ["get", "org.gnome.desktop.interface", "gtk-theme"]),
        new("gpu.glxinfo", "glxinfo", ["-B"]),
        new("gpu.nvidia-smi", "nvidia-smi", ["--query-gpu=name,driver_version,display_active,display_mode", "--format=csv,noheader"])
    ];

    public static async Task<IReadOnlyDictionary<string, string>> CollectAsync(
        DesktopSessionCapabilityReport report,
        CancellationToken cancellationToken)
    {
        if (!report.IsGuiSessionAvailable)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var availableTools = report.AvailableTools.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(report.DesktopSessionId) && availableTools.Contains("loginctl"))
        {
            var sessionOutput = await RunAsync(
                new DiagnosticCommand(
                    "session.loginctl",
                    "loginctl",
                    [
                        "show-session",
                        report.DesktopSessionId,
                        "-p",
                        "Id",
                        "-p",
                        "Name",
                        "-p",
                        "User",
                        "-p",
                        "Type",
                        "-p",
                        "Class",
                        "-p",
                        "Active",
                        "-p",
                        "State",
                        "-p",
                        "Remote",
                        "-p",
                        "Service",
                        "-p",
                        "Desktop"
                    ]),
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(sessionOutput))
            {
                diagnostics["session.loginctl"] = sessionOutput;
            }
        }

        foreach (var command in Commands)
        {
            if (!availableTools.Contains(command.Executable))
            {
                continue;
            }

            var output = await RunAsync(command, cancellationToken);
            if (!string.IsNullOrWhiteSpace(output))
            {
                diagnostics[command.Name] = output;
            }
        }

        return diagnostics;
    }

    private static async Task<string> RunAsync(DiagnosticCommand command, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeoutMilliseconds);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command.Executable,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                }
            };
            foreach (var argument in command.Arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                return string.Empty;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            return NormalizeOutput(output, error);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return string.Empty;
        }
    }

    private static string NormalizeOutput(string standardOutput, string standardError)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(standardOutput))
        {
            builder.AppendLine(standardOutput.Trim());
        }

        if (!string.IsNullOrWhiteSpace(standardError))
        {
            builder.AppendLine($"stderr: {standardError.Trim()}");
        }

        var normalized = builder.ToString().Trim();
        return normalized.Length <= MaxOutputChars
            ? normalized
            : $"{normalized[..MaxOutputChars]}...";
    }

    private sealed record DiagnosticCommand(
        string Name,
        string Executable,
        IReadOnlyList<string> Arguments);
}
