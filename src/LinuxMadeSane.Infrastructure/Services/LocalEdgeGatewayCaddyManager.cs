using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.EdgeGateway;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalEdgeGatewayCaddyManager(ILinuxCommandRunner commandRunner) : IEdgeGatewayCaddyManager
{
    private const string ServiceName = "caddy";
    private const string MainConfigPath = "/etc/caddy/Caddyfile";
    private const string ManagedRootDirectory = "/etc/caddy/linuxmadesane";
    private const string ManagedConfigPath = "/etc/caddy/linuxmadesane/edge-gateway.caddy";
    private const string ManagedBackupPath = "/etc/caddy/linuxmadesane/edge-gateway.caddy.bak";
    private const string ManagedImportLine = "import /etc/caddy/linuxmadesane/edge-gateway.caddy";
    private const string RemoteLmsRelayConfigPath = "/etc/caddy/linuxmadesane/remote-lms-relays.caddy";

    public async Task<EdgeGatewayCaddyApplyResult> ValidateAsync(string caddyfile, CancellationToken cancellationToken = default)
    {
        var logs = new List<OperationLogEntry>();
        var tempDirectory = CreateTemporaryDirectory();
        try
        {
            var tempMainConfigPath = Path.Combine(tempDirectory, Path.GetFileName(MainConfigPath));
            var tempManagedConfigPath = Path.Combine(tempDirectory, Path.GetFileName(ManagedConfigPath));
            var liveMainText = await ReadTextOrDefaultAsync(MainConfigPath, cancellationToken);

            await EnsureRemoteLmsRelayFileAsync(cancellationToken);
            await File.WriteAllTextAsync(tempManagedConfigPath, caddyfile, cancellationToken);
            await File.WriteAllTextAsync(tempMainConfigPath, EnsureManagedImport(liveMainText, tempManagedConfigPath), cancellationToken);
            var validation = await ValidateConfigurationAsync(tempMainConfigPath, cancellationToken);
            logs.Add(validation);
            return validation.Level == OperationLogLevel.Success
                ? new EdgeGatewayCaddyApplyResult(true, "Generated Edge Gateway Caddy config validates.", ManagedConfigPath, logs)
                : new EdgeGatewayCaddyApplyResult(false, FirstUsefulFailureLine(validation.StandardError, validation.StandardOutput) ?? "Caddy rejected the generated Edge Gateway config.", ManagedConfigPath, logs);
        }
        finally
        {
            DeleteDirectoryIfExists(tempDirectory);
        }
    }

    public async Task<EdgeGatewayCaddyApplyResult> ApplyAsync(string caddyfile, CancellationToken cancellationToken = default)
    {
        var logs = new List<OperationLogEntry>();
        var tempDirectory = CreateTemporaryDirectory();
        try
        {
            var liveMainText = await ReadTextOrDefaultAsync(MainConfigPath, cancellationToken);
            var previousManagedText = await ReadTextOrDefaultAsync(ManagedConfigPath, cancellationToken);
            var tempMainConfigPath = Path.Combine(tempDirectory, Path.GetFileName(MainConfigPath));
            var tempManagedConfigPath = Path.Combine(tempDirectory, Path.GetFileName(ManagedConfigPath));

            await EnsureRemoteLmsRelayFileAsync(cancellationToken);
            await File.WriteAllTextAsync(tempManagedConfigPath, caddyfile, cancellationToken);
            await File.WriteAllTextAsync(tempMainConfigPath, EnsureManagedImport(liveMainText, tempManagedConfigPath), cancellationToken);

            var validation = await ValidateConfigurationAsync(tempMainConfigPath, cancellationToken);
            logs.Add(validation);
            if (validation.Level == OperationLogLevel.Error)
            {
                return new EdgeGatewayCaddyApplyResult(false, FirstUsefulFailureLine(validation.StandardError, validation.StandardOutput) ?? "Caddy rejected the staged Edge Gateway config.", ManagedConfigPath, logs);
            }

            await EnsureDirectoryAsync(ManagedRootDirectory, cancellationToken);
            if (!string.IsNullOrWhiteSpace(previousManagedText))
            {
                await WriteTextAsync(ManagedBackupPath, previousManagedText, cancellationToken);
            }

            await WriteTextAsync(ManagedConfigPath, caddyfile, cancellationToken);
            await WriteTextAsync(MainConfigPath, EnsureManagedImport(liveMainText, ManagedConfigPath), cancellationToken);

            var reload = await commandRunner.RunAsync(
                new LinuxCommandRequest("systemctl", ["reload", ServiceName], true, TimeSpan.FromMinutes(1), "Reload Caddy after Edge Gateway update"),
                dryRun: false,
                cancellationToken);
            logs.Add(Map(reload, "Reload Caddy"));

            if (reload.ExitCode == 0)
            {
                return new EdgeGatewayCaddyApplyResult(true, "Edge Gateway Caddy config applied and Caddy reloaded.", ManagedConfigPath, logs);
            }

            if (!string.IsNullOrWhiteSpace(previousManagedText))
            {
                await WriteTextAsync(ManagedConfigPath, previousManagedText, cancellationToken);
            }

            return new EdgeGatewayCaddyApplyResult(false, FirstUsefulFailureLine(reload.StandardError, reload.StandardOutput) ?? "Caddy reload failed. Previous Edge Gateway config was restored.", ManagedConfigPath, logs);
        }
        finally
        {
            DeleteDirectoryIfExists(tempDirectory);
        }
    }

    public async Task<EdgeGatewayCaddyApplyResult> RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ManagedBackupPath))
        {
            return new EdgeGatewayCaddyApplyResult(false, "No Edge Gateway Caddy backup exists yet.", ManagedConfigPath, []);
        }

        var backupText = await ReadTextOrDefaultAsync(ManagedBackupPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(backupText))
        {
            return new EdgeGatewayCaddyApplyResult(false, "The Edge Gateway Caddy backup is empty.", ManagedConfigPath, []);
        }

        var validate = await ValidateAsync(backupText, cancellationToken);
        if (!validate.Success)
        {
            return validate with { Summary = $"Rollback blocked: {validate.Summary}" };
        }

        return await ApplyAsync(backupText, cancellationToken);
    }

    private async Task<OperationLogEntry> ValidateConfigurationAsync(string mainConfigPath, CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "caddy",
                ["validate", "--config", mainConfigPath, "--adapter", "caddyfile"],
                false,
                TimeSpan.FromSeconds(20),
                "Validate staged Edge Gateway Caddy configuration")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        return Map(result, "Validate Edge Gateway Caddy configuration");
    }

    private async Task EnsureDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(path);
            return;
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }

        await RunRequiredCommandAsync("install", ["-d", "-m", "0755", path], $"Create directory {path}", cancellationToken);
    }

    private async Task EnsureRemoteLmsRelayFileAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(RemoteLmsRelayConfigPath))
        {
            return;
        }

        await EnsureDirectoryAsync(ManagedRootDirectory, cancellationToken);
        await WriteTextAsync(
            RemoteLmsRelayConfigPath,
            "# Generated by Linux Made Sane. Do not edit manually.\n# Remote LMS relay routes. Imported inside the Edge Gateway Caddy listener.\n",
            cancellationToken);
    }

    private async Task WriteTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        try
        {
            await File.WriteAllTextAsync(path, content, cancellationToken);
            return;
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }

        var temporaryFilePath = Path.Combine(CreateTemporaryDirectory(), Path.GetRandomFileName());
        try
        {
            await File.WriteAllTextAsync(temporaryFilePath, content, cancellationToken);
            await RunRequiredCommandAsync(
                "install",
                ["-D", "-m", "0644", temporaryFilePath, path],
                $"Write Caddy configuration {path}",
                cancellationToken);
        }
        finally
        {
            var tempDirectory = Path.GetDirectoryName(temporaryFilePath);
            if (File.Exists(temporaryFilePath))
            {
                File.Delete(temporaryFilePath);
            }

            if (!string.IsNullOrWhiteSpace(tempDirectory))
            {
                DeleteDirectoryIfExists(tempDirectory);
            }
        }
    }

    private async Task RunRequiredCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(fileName, arguments, true, TimeSpan.FromMinutes(1), description),
            dryRun: false,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{description} failed: {FirstUsefulFailureLine(result.StandardError, result.StandardOutput) ?? $"exit code {result.ExitCode}"}");
        }
    }

    private static async Task<string> ReadTextOrDefaultAsync(string path, CancellationToken cancellationToken) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : string.Empty;

    private static string EnsureManagedImport(string currentText, string importPath)
    {
        var cleanedLines = (currentText ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Where(static line => !IsEdgeGatewayImportOrHeading(line))
            .Select(static line => line.TrimEnd())
            .ToList();

        while (cleanedLines.Count > 0 && string.IsNullOrWhiteSpace(cleanedLines[^1]))
        {
            cleanedLines.RemoveAt(cleanedLines.Count - 1);
        }

        var importLine = $"import {importPath}";
        if (cleanedLines.Count == 0)
        {
            return string.Join(
                Environment.NewLine,
                "# Managed by Linux Made Sane",
                "# Linux Made Sane Edge Gateway routes",
                importLine,
                string.Empty);
        }

        cleanedLines.Add(string.Empty);
        cleanedLines.Add("# Linux Made Sane Edge Gateway routes");
        cleanedLines.Add(importLine);
        cleanedLines.Add(string.Empty);
        return string.Join(Environment.NewLine, cleanedLines);
    }

    private static bool IsEdgeGatewayImportOrHeading(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Equals("# Linux Made Sane Edge Gateway routes", StringComparison.Ordinal) ||
               trimmed.Equals(ManagedImportLine, StringComparison.Ordinal) ||
               (trimmed.StartsWith("import ", StringComparison.Ordinal) &&
                trimmed.EndsWith("/edge-gateway.caddy", StringComparison.Ordinal));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lms-edge-gateway-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static OperationLogEntry Map(LinuxCommandResult result, string message) =>
        new(
            result.CompletedAt,
            result.ExitCode == 0 ? OperationLogLevel.Success : OperationLogLevel.Error,
            message,
            result.CommandText,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError);

    private static string? FirstUsefulFailureLine(params string?[] values)
    {
        var lines = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(static value => value!.Split('\n'))
            .Select(static line => line.Trim())
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (lines.Length == 0)
        {
            return null;
        }

        var selected = lines.FirstOrDefault(static line =>
                           line.Contains("\"level\":\"error\"", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("\"error\"", StringComparison.OrdinalIgnoreCase)) ??
                       lines.FirstOrDefault(static line =>
                           !line.Contains("\"level\":\"info\"", StringComparison.OrdinalIgnoreCase) &&
                           !line.Contains("\"level\":\"warn\"", StringComparison.OrdinalIgnoreCase)) ??
                       lines[0];
        return SimplifyCaddyJsonLine(selected);
    }

    private static string SimplifyCaddyJsonLine(string line)
    {
        if (!line.StartsWith('{'))
        {
            return line;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return error.GetString() ?? line;
            }

            if (root.TryGetProperty("msg", out var message) && message.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return message.GetString() ?? line;
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return line;
    }
}
