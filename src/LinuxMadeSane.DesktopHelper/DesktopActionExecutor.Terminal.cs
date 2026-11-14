// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Models.DesktopSession;
namespace LinuxMadeSane.DesktopHelper;

public sealed partial class DesktopActionExecutor
{
    private async Task<DesktopSessionActionResult> ExecuteTerminalCommandAsync(DesktopSessionActionRequest request, CancellationToken token)
    {
        var command = request.Arguments.GetValueOrDefault("command") ?? "";
        if (string.IsNullOrWhiteSpace(command) || command.Length > 64000 || command.Contains('\0'))
            return BuildResult(request, false, "Invalid command.", "The approved command was empty or invalid.", new Dictionary<string, string>());
        var directory = request.Arguments.GetValueOrDefault("directory");
        if (string.IsNullOrWhiteSpace(directory)) directory = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(directory)) directory = Environment.GetEnvironmentVariable("HOME") ?? "/";
        var marker = "LMS-PWD-" + Guid.NewGuid().ToString("N");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var start = new ProcessStartInfo("/bin/bash") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        start.ArgumentList.Add("--noprofile"); start.ArgumentList.Add("--norc"); start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command + "\n__lms_status=$?\nprintf '\\n" + marker + ":%s\\n' \"$PWD\"\nexit \"$__lms_status\"");
        // Retain this helper's real user, display, Xauthority and D-Bus environment.
        // Commands never run as the LMS service account and stdin cannot prompt in chat.
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start(); process.StandardInput.Close();
            var outputTask = ReadBoundedAsync(process.StandardOutput, timeout.Token); var errorTask = ReadBoundedAsync(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token); var output = await outputTask; var error = await errorTask;
            var match = Regex.Match(output, "\\n" + marker + ":([^\\r\\n]*)\\r?\\n?$");
            if (match.Success) { directory = match.Groups[1].Value; output = output[..match.Index]; }
            return BuildResult(request, process.ExitCode == 0, "Command finished.", error, new Dictionary<string, string>
            {
                ["exitCode"] = process.ExitCode.ToString(),
                ["stdout"] = output,
                ["stderr"] = error,
                ["directory"] = directory
            });
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (token.IsCancellationRequested) throw;
            return BuildResult(request, false, "Command timed out.", "The command exceeded two minutes and was stopped.", new Dictionary<string, string> { ["exitCode"] = "124", ["stderr"] = "Command timed out", ["directory"] = directory });
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[4096]; var text = new StringBuilder(); var truncated = false; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        { text.Append(buffer, 0, count); if (text.Length > 32000) { text.Remove(0, text.Length - 32000); truncated = true; } }
        return (truncated ? "[earlier output truncated]\n" : "") + text;
    }
}
