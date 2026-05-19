// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Caddy;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SqliteCaddyIntegrationDataService(
    LinuxMadeSaneDbContext dbContext,
    IPackageManagementService packageManagementService,
    IServiceManagementService serviceManagementService,
    ILinuxCommandRunner commandRunner) : ICaddyIntegrationDataService
{
    private const string PackageName = "caddy";
    private const string ServiceName = "caddy";
    private const string MainConfigPath = "/etc/caddy/Caddyfile";
    private const string ManagedRootDirectory = "/etc/caddy/linuxmadesane";
    private const string ManagedConfigPath = "/etc/caddy/linuxmadesane/reverse-proxies.caddy";
    private const string ManagedImportLine = "import /etc/caddy/linuxmadesane/reverse-proxies.caddy";

    public async Task<CaddyIntegrationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var routesTask = dbContext.CaddyProxyRoutes
            .AsNoTracking()
            .OrderBy(route => route.Name)
            .ToArrayAsync(cancellationToken);
        var packagesTask = packageManagementService.InspectAsync([PackageName], cancellationToken);
        var servicesTask = serviceManagementService.InspectAsync([ServiceName], cancellationToken);

        await Task.WhenAll(routesTask, packagesTask, servicesTask);

        var package = packagesTask.Result.FirstOrDefault();
        var service = servicesTask.Result.FirstOrDefault();
        var isInstalled = package?.IsInstalled == true;
        var mainConfigText = await ReadTextOrDefaultAsync(MainConfigPath, cancellationToken);
        var importConfigured = mainConfigText.Contains(ManagedImportLine, StringComparison.Ordinal);
        var version = isInstalled ? await GetInstalledVersionAsync(cancellationToken) : string.Empty;
        var validation = isInstalled
            ? await ValidateLiveConfigurationAsync(cancellationToken)
            : new CaddyOperationResult(false, "Caddy is not installed on this LMS host yet.", []);

        return new CaddyIntegrationSnapshot(
            isInstalled,
            string.IsNullOrWhiteSpace(version) ? package?.Version ?? string.Empty : version,
            service?.IsActive == true,
            service?.IsEnabled == true,
            importConfigured,
            validation.Success,
            validation.Summary,
            MainConfigPath,
            ManagedConfigPath,
            routesTask.Result.Select(Map).ToArray());
    }

    public async Task<CaddyProxyRouteDefinition?> GetRouteAsync(Guid id, CancellationToken cancellationToken = default) =>
        MapOrNull(await dbContext.CaddyProxyRoutes
            .AsNoTracking()
            .SingleOrDefaultAsync(route => route.Id == id, cancellationToken));

    public async Task SaveRouteAsync(CaddyProxyRouteDefinition route, CancellationToken cancellationToken = default)
    {
        await EnsureInstalledAsync(cancellationToken);

        var entity = await dbContext.CaddyProxyRoutes.SingleOrDefaultAsync(item => item.Id == route.Id, cancellationToken);
        if (entity is null)
        {
            dbContext.CaddyProxyRoutes.Add(Map(route));
        }
        else
        {
            Apply(entity, route);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await ApplyManagedConfigurationAsync(cancellationToken);
    }

    public async Task DeleteRouteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.CaddyProxyRoutes.SingleOrDefaultAsync(route => route.Id == id, cancellationToken);
        if (entity is null)
        {
            return;
        }

        dbContext.CaddyProxyRoutes.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        var package = (await packageManagementService.InspectAsync([PackageName], cancellationToken)).FirstOrDefault();
        if (package?.IsInstalled == true)
        {
            await ApplyManagedConfigurationAsync(cancellationToken);
        }
    }

    public async Task<CaddyOperationResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        var logs = new List<OperationLogEntry>();
        logs.AddRange(await packageManagementService.ApplyActionsAsync(
            [
                new PackageAction(
                    PackageActionKind.Install,
                    PackageName,
                    "Install Caddy reverse proxy on the LMS host.",
                    false,
                    "sudo apt-get update && sudo apt-get install -y caddy")
            ],
            dryRun: false,
            cancellationToken));

        if (logs.Any(log => log.Level == OperationLogLevel.Error))
        {
            return BuildFailureResult(logs, "Caddy install failed.");
        }

        await ApplyManagedConfigurationAsync(cancellationToken);
        logs.AddRange(await serviceManagementService.ApplyActionsAsync(
            [
                new ServiceAction(ServiceActionKind.Enable, ServiceName, "Enable Caddy at boot.", false, "sudo systemctl enable caddy"),
                new ServiceAction(ServiceActionKind.Start, ServiceName, "Start Caddy now.", false, "sudo systemctl start caddy")
            ],
            dryRun: false,
            cancellationToken));

        return BuildResult(logs, "Installed and started Caddy.");
    }

    public async Task<CaddyOperationResult> ReloadAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInstalledAsync(cancellationToken);
        await ApplyManagedConfigurationAsync(cancellationToken);

        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "systemctl",
                ["reload", ServiceName],
                true,
                TimeSpan.FromMinutes(1),
                "Reload Caddy service"),
            dryRun: false,
            cancellationToken);

        var log = Map(result, "Reload Caddy");
        return result.ExitCode == 0
            ? new CaddyOperationResult(true, "Reloaded Caddy.", [log])
            : BuildFailureResult([log], "Caddy reload failed.");
    }

    public async Task<CaddyOperationResult> RestartAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInstalledAsync(cancellationToken);
        await ApplyManagedConfigurationAsync(cancellationToken);

        var logs = await serviceManagementService.ApplyActionsAsync(
            [new ServiceAction(ServiceActionKind.Restart, ServiceName, "Restart Caddy now.", false, "sudo systemctl restart caddy")],
            dryRun: false,
            cancellationToken);

        return BuildResult(logs, "Restarted Caddy.");
    }

    private async Task ApplyManagedConfigurationAsync(CancellationToken cancellationToken)
    {
        var routes = await dbContext.CaddyProxyRoutes
            .AsNoTracking()
            .OrderBy(route => route.Name)
            .ThenBy(route => route.Hostname)
            .ToArrayAsync(cancellationToken);
        var liveMainText = await ReadTextOrDefaultAsync(MainConfigPath, cancellationToken);
        var liveManagedText = RenderManagedConfiguration(routes.Select(Map).ToArray());

        var tempDirectory = CreateTemporaryDirectory();
        try
        {
            var tempMainConfigPath = Path.Combine(tempDirectory, Path.GetFileName(MainConfigPath));
            var tempManagedConfigPath = Path.Combine(tempDirectory, Path.GetFileName(ManagedConfigPath));

            await File.WriteAllTextAsync(tempManagedConfigPath, liveManagedText, cancellationToken);
            await File.WriteAllTextAsync(
                tempMainConfigPath,
                EnsureManagedImport(liveMainText, tempManagedConfigPath),
                cancellationToken);

            await ValidateConfigurationAsync(tempMainConfigPath, cancellationToken);
            await EnsureDirectoryAsync(ManagedRootDirectory, cancellationToken);
            await WriteTextAsync(ManagedConfigPath, liveManagedText, cancellationToken);
            await WriteTextAsync(MainConfigPath, EnsureManagedImport(liveMainText, ManagedConfigPath), cancellationToken);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private async Task EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        var package = (await packageManagementService.InspectAsync([PackageName], cancellationToken)).FirstOrDefault();
        if (package?.IsInstalled == true)
        {
            return;
        }

        throw new InvalidOperationException("Caddy is not installed on this LMS host yet.");
    }

    private async Task<string> GetInstalledVersionAsync(CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "caddy",
                ["version"],
                false,
                TimeSpan.FromSeconds(15),
                "Inspect Caddy version")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        return result.ExitCode == 0
            ? result.StandardOutput.Trim()
            : string.Empty;
    }

    private async Task<CaddyOperationResult> ValidateLiveConfigurationAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(MainConfigPath))
        {
            return new CaddyOperationResult(false, "Caddy is installed, but /etc/caddy/Caddyfile does not exist yet.", []);
        }

        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "caddy",
                ["validate", "--config", MainConfigPath, "--adapter", "caddyfile"],
                false,
                TimeSpan.FromSeconds(20),
                "Validate live Caddy configuration")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        if (result.ExitCode == 0)
        {
            return new CaddyOperationResult(true, "Caddy configuration validates cleanly.", [Map(result, "Validate Caddy configuration")]);
        }

        return BuildFailureResult([Map(result, "Validate Caddy configuration")],
            FirstNonEmptyLine(result.StandardError, result.StandardOutput) ?? "Caddy configuration is not valid.");
    }

    private async Task ValidateConfigurationAsync(string mainConfigPath, CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "caddy",
                ["validate", "--config", mainConfigPath, "--adapter", "caddyfile"],
                false,
                TimeSpan.FromSeconds(20),
                "Validate staged Caddy configuration")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        if (result.ExitCode == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            FirstNonEmptyLine(result.StandardError, result.StandardOutput) ?? "Caddy rejected the staged configuration.");
    }

    private static string RenderManagedConfiguration(IReadOnlyList<CaddyProxyRouteDefinition> routes)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Managed by Linux Made Sane");
        builder.AppendLine("# Edit routes in LMS, not in this generated file.");
        builder.AppendLine();

        foreach (var route in routes)
        {
            builder.AppendLine($"# Route: {route.Name}");
            if (!string.IsNullOrWhiteSpace(route.Description))
            {
                builder.AppendLine($"# {SanitizeComment(route.Description)}");
            }

            var address = route.EnableTls ? route.Hostname : $"http://{route.Hostname}";
            builder.AppendLine($"{address} {{");
            builder.AppendLine("    encode zstd gzip");
            builder.AppendLine($"    reverse_proxy {route.UpstreamUrl}");
            builder.AppendLine("}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string EnsureManagedImport(string currentText, string importPath)
    {
        var normalizedCurrentText = string.IsNullOrWhiteSpace(currentText)
            ? string.Empty
            : currentText.TrimEnd() + Environment.NewLine;
        var importLine = $"import {importPath}";
        if (normalizedCurrentText.Contains(importLine, StringComparison.Ordinal))
        {
            return normalizedCurrentText;
        }

        if (normalizedCurrentText.Length == 0)
        {
            return $"# Managed by Linux Made Sane{Environment.NewLine}{importLine}{Environment.NewLine}";
        }

        return $"{normalizedCurrentText}{Environment.NewLine}# Linux Made Sane managed reverse proxies{Environment.NewLine}{importLine}{Environment.NewLine}";
    }

    private async Task<string> ReadTextOrDefaultAsync(string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }

        return string.Empty;
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

        await RunRequiredCommandAsync(
            "install",
            ["-d", "-m", "0755", path],
            $"Create directory {path}",
            cancellationToken);
    }

    private async Task WriteTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directoryPath = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            try
            {
                Directory.CreateDirectory(directoryPath);
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

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

            if (!string.IsNullOrWhiteSpace(tempDirectory) && Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
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

        if (result.ExitCode == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{description} failed: {FirstNonEmptyLine(result.StandardError, result.StandardOutput) ?? $"exit code {result.ExitCode}"}");
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lms-caddy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
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

    private static CaddyProxyRouteDefinition Map(CaddyProxyRouteEntity entity) =>
        new(
            entity.Id,
            entity.Name,
            entity.Hostname,
            entity.UpstreamUrl,
            entity.Description,
            entity.EnableTls,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc);

    private static CaddyProxyRouteDefinition? MapOrNull(CaddyProxyRouteEntity? entity) =>
        entity is null ? null : Map(entity);

    private static CaddyProxyRouteEntity Map(CaddyProxyRouteDefinition route) =>
        new()
        {
            Id = route.Id,
            Name = route.Name,
            Hostname = route.Hostname,
            UpstreamUrl = route.UpstreamUrl,
            Description = route.Description,
            EnableTls = route.EnableTls,
            CreatedAtUtc = route.CreatedAtUtc,
            UpdatedAtUtc = route.UpdatedAtUtc
        };

    private static void Apply(CaddyProxyRouteEntity entity, CaddyProxyRouteDefinition route)
    {
        entity.Name = route.Name;
        entity.Hostname = route.Hostname;
        entity.UpstreamUrl = route.UpstreamUrl;
        entity.Description = route.Description;
        entity.EnableTls = route.EnableTls;
        entity.CreatedAtUtc = route.CreatedAtUtc;
        entity.UpdatedAtUtc = route.UpdatedAtUtc;
    }

    private static CaddyOperationResult BuildResult(IReadOnlyList<OperationLogEntry> logs, string successSummary)
    {
        var failed = logs.FirstOrDefault(log => log.Level == OperationLogLevel.Error);
        return failed is null
            ? new CaddyOperationResult(true, successSummary, logs)
            : BuildFailureResult(logs, failed.Message);
    }

    private static CaddyOperationResult BuildFailureResult(IReadOnlyList<OperationLogEntry> logs, string summary) =>
        new(false, summary, logs);

    private static string? FirstNonEmptyLine(params string[] values) =>
        values
            .SelectMany(value => value.Split('\n'))
            .Select(line => line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

    private static string SanitizeComment(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
