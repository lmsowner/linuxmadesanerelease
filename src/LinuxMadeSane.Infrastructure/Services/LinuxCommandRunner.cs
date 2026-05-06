using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LinuxCommandRunner(ILogger<LinuxCommandRunner> logger) : ILinuxCommandRunner
{
    public async Task<LinuxCommandResult> RunAsync(
        LinuxCommandRequest request,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var commandText = RenderCommand(request);
        var operationLabel = ResolveOperationLabel(request, commandText);
        var startedAt = DateTimeOffset.UtcNow;
        ProcessStartInfo? startInfo = null;

        LogCommandRequested(request, operationLabel, commandText);

        if (dryRun)
        {
            return new LinuxCommandResult(
                commandText,
                0,
                string.Empty,
                string.Empty,
                startedAt,
                DateTimeOffset.UtcNow,
                true);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout > TimeSpan.Zero)
        {
            timeoutCts.CancelAfter(request.Timeout);
        }

        try
        {
            startInfo = BuildStartInfo(request);
            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(timeoutCts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            LogCommandCompleted(request, operationLabel, commandText, process.ExitCode);

            return new LinuxCommandResult(
                commandText,
                process.ExitCode,
                stdout,
                stderr,
                startedAt,
                DateTimeOffset.UtcNow,
                false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Linux command timed out: {Operation}", operationLabel);

            return new LinuxCommandResult(
                commandText,
                124,
                string.Empty,
                $"Command timed out after {request.Timeout}.",
                startedAt,
                DateTimeOffset.UtcNow,
                false);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Linux command cancelled: {Operation}", operationLabel);
            throw;
        }
        catch (Win32Exception exception) when (IsExecutableMissing(exception))
        {
            var executableName = startInfo?.FileName ?? request.FileName;
            var message = $"Command '{executableName}' was not found on this LMS host.";

            LogMissingExecutable(request, operationLabel, commandText, message);

            return new LinuxCommandResult(
                commandText,
                127,
                string.Empty,
                message,
                startedAt,
                DateTimeOffset.UtcNow,
                false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Linux command failed before completion: {Operation}", operationLabel);

            return new LinuxCommandResult(
                commandText,
                -1,
                string.Empty,
                exception.Message,
                startedAt,
                DateTimeOffset.UtcNow,
                false);
        }
    }

    private static bool IsExecutableMissing(Win32Exception exception) =>
        exception.NativeErrorCode == 2 ||
        exception.Message.Contains("No such file", StringComparison.OrdinalIgnoreCase);

    private void LogCommandRequested(LinuxCommandRequest request, string operationLabel, string commandText)
    {
        if (request.IsOptionalExternalTool)
        {
            logger.LogDebug("Linux command requested: {Operation}", operationLabel);
            return;
        }

        logger.LogInformation("Linux command requested: {Operation}", operationLabel);
    }

    private void LogCommandCompleted(LinuxCommandRequest request, string operationLabel, string commandText, int exitCode)
    {
        if (request.IsOptionalExternalTool)
        {
            logger.LogDebug("Linux command completed: {Operation} => {ExitCode}", operationLabel, exitCode);
            return;
        }

        logger.LogInformation("Linux command completed: {Operation} => {ExitCode}", operationLabel, exitCode);
    }

    private void LogMissingExecutable(LinuxCommandRequest request, string operationLabel, string commandText, string message)
    {
        if (request.IsOptionalExternalTool)
        {
            logger.LogDebug(
                "Linux command executable was not found: {Operation}. {FailureMessage}",
                operationLabel,
                message);
            return;
        }

        logger.LogWarning(
            "Linux command executable was not found: {Operation}. {FailureMessage}",
            operationLabel,
            message);
    }

    private static string ResolveOperationLabel(LinuxCommandRequest request, string commandText) =>
        string.IsNullOrWhiteSpace(request.Description)
            ? commandText
            : request.Description.Trim();

    private static ProcessStartInfo BuildStartInfo(LinuxCommandRequest request)
    {
        var requiresSudo = request.RequiresSudo && !string.Equals(Environment.UserName, "root", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = requiresSudo ? "sudo" : request.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        if (requiresSudo)
        {
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add(request.FileName);
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string RenderCommand(LinuxCommandRequest request)
    {
        var builder = new StringBuilder();
        if (request.RequiresSudo && !string.Equals(Environment.UserName, "root", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append("sudo -n ");
        }

        builder.Append(request.FileName);
        foreach (var argument in request.Arguments)
        {
            builder.Append(' ');
            builder.Append(Quote(argument));
        }

        return builder.ToString();
    }

    private static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        return value.Any(char.IsWhiteSpace) || value.Contains('"') || value.Contains('\'')
            ? $"'{value.Replace("'", "'\"'\"'")}'"
            : value;
    }
}
