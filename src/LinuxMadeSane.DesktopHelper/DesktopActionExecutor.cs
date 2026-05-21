// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Models.DesktopSession;

namespace LinuxMadeSane.DesktopHelper;

public sealed partial class DesktopActionExecutor(DesktopSessionEnvironmentDetector detector)
{
    private const int CommandTimeoutMilliseconds = 3000;

    public async Task<DesktopSessionActionResult> ExecuteAsync(
        DesktopSessionActionRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.ActionKind, DesktopSessionActionKinds.SetKeyboardLayout, StringComparison.Ordinal))
        {
            return BuildResult(
                request,
                false,
                "Desktop action is not allowed.",
                $"Unsupported action: {request.ActionKind}",
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var layout = request.Arguments.GetValueOrDefault("layout")?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!KeyboardLayoutPattern().IsMatch(layout))
        {
            return BuildResult(
                request,
                false,
                "Keyboard layout was not changed.",
                "The requested keyboard layout code was invalid.",
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        return await SetKeyboardLayoutAsync(request, layout, cancellationToken);
    }

    private async Task<DesktopSessionActionResult> SetKeyboardLayoutAsync(
        DesktopSessionActionRequest request,
        string layout,
        CancellationToken cancellationToken)
    {
        var commandResults = new List<CommandResult>
        {
            await RunAsync("setxkbmap", [layout], cancellationToken),
            await RunAsync("xfconf-query", ["-c", "keyboard-layout", "-p", "/Default/XkbLayout", "-n", "-t", "string", "-s", layout], cancellationToken),
            await RunAsync("xfconf-query", ["-c", "keyboard-layout", "-p", "/Default/XkbDisable", "-n", "-t", "bool", "-s", "false"], cancellationToken),
            await RunAsync("gsettings", ["set", "org.gnome.desktop.input-sources", "sources", $"[('xkb', '{layout}')]"], cancellationToken)
        };
        var successCount = commandResults.Count(result => result.Succeeded);

        var report = detector.DetectCurrent();
        var diagnostics = await DesktopReadOnlyDiagnosticsCollector.CollectAsync(report, cancellationToken);
        var detail = string.Join(
            Environment.NewLine,
            commandResults.Select(result => $"{result.Executable}: {(result.Succeeded ? "ok" : "not applied")} {result.Output}".Trim()));

        return BuildResult(
            request,
            successCount > 0,
            successCount > 0
                ? $"Keyboard layout set to {layout}."
                : $"Keyboard layout could not be set to {layout}.",
            detail,
            diagnostics);
    }

    private static async Task<CommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeoutMilliseconds);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                }
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                return new CommandResult(executable, false, "could not start");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = NormalizeOutput(await outputTask, await errorTask);
            return new CommandResult(executable, process.ExitCode == 0, output);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new CommandResult(executable, false, exception.Message);
        }
    }

    private static string NormalizeOutput(string standardOutput, string standardError)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(standardOutput))
        {
            builder.Append(standardOutput.Trim());
        }

        if (!string.IsNullOrWhiteSpace(standardError))
        {
            if (builder.Length > 0)
            {
                builder.Append(" | ");
            }

            builder.Append(standardError.Trim());
        }

        return builder.ToString();
    }

    private static DesktopSessionActionResult BuildResult(
        DesktopSessionActionRequest request,
        bool succeeded,
        string summary,
        string detail,
        IReadOnlyDictionary<string, string> diagnostics) =>
        new(
            request.RequestId,
            request.ActionKind,
            succeeded,
            summary,
            detail,
            diagnostics,
            DateTimeOffset.UtcNow);

    [GeneratedRegex("^[a-z]{2,3}([_+-][a-z0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyboardLayoutPattern();

    private sealed record CommandResult(string Executable, bool Succeeded, string Output);
}
