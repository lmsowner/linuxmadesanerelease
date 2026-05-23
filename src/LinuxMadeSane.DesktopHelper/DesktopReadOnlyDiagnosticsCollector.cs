// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Diagnostics;
using System.Text;
using LinuxMadeSane.Core.Models.DesktopSession;

namespace LinuxMadeSane.DesktopHelper;

internal static class DesktopReadOnlyDiagnosticsCollector
{
    private const int CommandTimeoutMilliseconds = 1500;
    private const int MaxConcurrentCommands = 6;
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

        using var throttle = new SemaphoreSlim(MaxConcurrentCommands);
        var commandTasks = BuildCommands(report)
            .Where(command => availableTools.Contains(command.Executable))
            .Select(command => RunThrottledAsync(command, throttle, cancellationToken))
            .ToArray();

        foreach (var result in await Task.WhenAll(commandTasks))
        {
            if (!string.IsNullOrWhiteSpace(result.Output))
            {
                diagnostics[result.Name] = result.Output;
            }
        }

        return diagnostics;
    }

    private static async Task<DiagnosticResult> RunThrottledAsync(
        DiagnosticCommand command,
        SemaphoreSlim throttle,
        CancellationToken cancellationToken)
    {
        await throttle.WaitAsync(cancellationToken);
        try
        {
            return new DiagnosticResult(command.Name, await RunAsync(command, cancellationToken));
        }
        finally
        {
            throttle.Release();
        }
    }

    private static IEnumerable<DiagnosticCommand> BuildCommands(DesktopSessionCapabilityReport report)
    {
        foreach (var command in Commands)
        {
            yield return command;
        }

        if (!string.IsNullOrWhiteSpace(report.UserName) &&
            !string.Equals(report.UserName, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            yield return new DiagnosticCommand(
                "process.user-top-memory",
                "ps",
                [
                    "-u",
                    report.UserName,
                    "-o",
                    "pid=,ppid=,comm=,%cpu=,%mem=,rss=,vsz=,etimes=,stat=,args=",
                    "--sort=-rss"
                ]);
        }

        yield return new DiagnosticCommand("process.system-memory", "free", ["-h"]);
        yield return new DiagnosticCommand(
            "process.system-top-memory",
            "ps",
            [
                "-eo",
                "pid=,user=,comm=,%cpu=,%mem=,rss=,stat=,args=",
                "--sort=-rss"
            ]);
        yield return new DiagnosticCommand("windows.wmctrl", "wmctrl", ["-lpG"]);
        yield return new DiagnosticCommand("input.xinput", "xinput", ["--list"]);
        yield return new DiagnosticCommand("audio.pactl-info", "pactl", ["info"]);
        yield return new DiagnosticCommand("audio.pactl-sinks", "pactl", ["list", "short", "sinks"]);
        yield return new DiagnosticCommand("audio.pactl-sources", "pactl", ["list", "short", "sources"]);
        yield return new DiagnosticCommand("bluetooth.controllers", "bluetoothctl", ["--timeout", "1", "list"]);
        yield return new DiagnosticCommand("bluetooth.default-controller", "bluetoothctl", ["--timeout", "1", "show"]);
        yield return new DiagnosticCommand("bluetooth.devices", "bluetoothctl", ["--timeout", "1", "devices"]);
        yield return new DiagnosticCommand("bluetooth.paired-devices", "bluetoothctl", ["--timeout", "1", "paired-devices"]);
        yield return new DiagnosticCommand("bluetooth.connected-devices", "bluetoothctl", ["--timeout", "1", "devices", "Connected"]);
        yield return new DiagnosticCommand("bluetooth.rfkill", "rfkill", ["list", "bluetooth"]);
        yield return new DiagnosticCommand(
            "bluetooth.system-service",
            "systemctl",
            [
                "show",
                "bluetooth.service",
                "-p",
                "ActiveState",
                "-p",
                "SubState",
                "-p",
                "UnitFileState",
                "--no-pager"
            ]);
        yield return new DiagnosticCommand("network.nmcli-devices", "nmcli", ["device", "status"]);
        yield return new DiagnosticCommand("power.upower-devices", "upower", ["-e"]);
        yield return new DiagnosticCommand("services.user-failed", "systemctl", ["--user", "--failed", "--no-pager"]);
        yield return new DiagnosticCommand("logs.user-warnings", "journalctl", ["--user", "-p", "warning", "-n", "60", "--no-pager"]);
    }

    private static async Task<string> RunAsync(DiagnosticCommand command, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeoutMilliseconds);
        Process? process = null;

        try
        {
            process = new Process
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
        catch (OperationCanceledException)
        {
            TryKill(process);
            return string.Empty;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return string.Empty;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
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

    private sealed record DiagnosticResult(string Name, string Output);
}
