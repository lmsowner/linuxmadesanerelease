// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
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
        await ReloadCaddyServiceAsync(cancellationToken);
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
            await ReloadCaddyServiceAsync(cancellationToken);
        }
    }

    public async Task<CaddyOperationResult> CheckRouteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var route = await GetRouteAsync(id, cancellationToken);
        if (route is null)
        {
            return new CaddyOperationResult(false, "Caddy route is not saved in LMS.", [
                BuildLog(OperationLogLevel.Error, "Route is not present in the LMS database.")
            ]);
        }

        var logs = new List<OperationLogEntry>();
        var package = (await packageManagementService.InspectAsync([PackageName], cancellationToken)).FirstOrDefault();
        if (package?.IsInstalled != true)
        {
            logs.Add(BuildLog(OperationLogLevel.Error, "Caddy is not installed on this LMS host."));
            return BuildFailureResult(logs, "Caddy route check failed: Caddy is not installed.");
        }

        var services = await serviceManagementService.InspectAsync([ServiceName], cancellationToken);
        var service = services.FirstOrDefault();
        logs.Add(BuildLog(
            service?.IsActive == true ? OperationLogLevel.Success : OperationLogLevel.Error,
            service?.IsActive == true ? "Caddy service is active." : "Caddy service is not running."));

        var mainConfigText = await ReadTextOrDefaultAsync(MainConfigPath, cancellationToken);
        logs.Add(BuildLog(
            mainConfigText.Contains(ManagedImportLine, StringComparison.Ordinal) ? OperationLogLevel.Success : OperationLogLevel.Error,
            mainConfigText.Contains(ManagedImportLine, StringComparison.Ordinal)
                ? "Main Caddyfile imports the LMS managed include."
                : "Main Caddyfile does not import the LMS managed include."));

        var managedConfigText = await ReadTextOrDefaultAsync(ManagedConfigPath, cancellationToken);
        logs.Add(BuildLog(
            ManagedConfigurationContainsRoute(managedConfigText, route) ? OperationLogLevel.Success : OperationLogLevel.Error,
            ManagedConfigurationContainsRoute(managedConfigText, route)
                ? "Managed Caddy include contains this route."
                : "Managed Caddy include does not contain this route. Save or reload the route so LMS writes it to Caddy."));

        var validation = await ValidateLiveConfigurationAsync(cancellationToken);
        logs.AddRange(validation.Logs);

        if (route.Kind == CaddyProxyRouteKind.PortForward)
        {
            logs.Add(await CheckTcpEndpointAsync(
                "Destination target accepts TCP",
                route.DestinationIp,
                route.DestinationPort,
                cancellationToken));
            logs.Add(await CheckDestinationProtocolAsync(route, cancellationToken));

            var sourceProbeHost = ResolveSourceProbeHost(route.SourceIp);
            logs.Add(await CheckTcpEndpointAsync(
                "Source listener accepts TCP",
                sourceProbeHost,
                route.SourcePort,
                cancellationToken));

            logs.Add(await CheckListenerSnapshotAsync(route, cancellationToken));
        }

        var failed = logs.FirstOrDefault(log => log.Level == OperationLogLevel.Error);
        return failed is null
            ? new CaddyOperationResult(true, "Caddy route check passed.", logs)
            : BuildFailureResult(logs, $"Caddy route check found a problem: {failed.Message}");
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
            FirstCaddyProblemLine(result.StandardError, result.StandardOutput) ?? "Caddy configuration is not valid.");
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
            FirstCaddyProblemLine(result.StandardError, result.StandardOutput) ?? "Caddy rejected the staged configuration.");
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

            if (route.Kind == CaddyProxyRouteKind.PortForward)
            {
                builder.Append(RenderPortForwardBlock(route));
            }
            else
            {
                var address = route.EnableTls ? route.Hostname : $"http://{route.Hostname}";
                builder.AppendLine($"{address} {{");
                builder.AppendLine("    encode zstd gzip");
                builder.AppendLine($"    reverse_proxy {route.UpstreamUrl}");
                builder.AppendLine("}");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string RenderPortForwardBlock(CaddyProxyRouteDefinition route)
    {
        var builder = new StringBuilder();
        var listenPort = Math.Clamp(route.SourcePort, 1, 65535);
        var sourceIp = CaddyBindAddressFormatter.NormalizeCsv(route.SourceIp);
        var targetUrl = BuildPortForwardTargetUrl(route);

        builder.AppendLine($"http://:{listenPort} {{");
        if (!CaddyBindAddressFormatter.IsAnyAddressList(sourceIp))
        {
            builder.AppendLine($"    bind {CaddyBindAddressFormatter.ToCaddyBindArguments(sourceIp)}");
        }

        builder.AppendLine("    encode zstd gzip");
        if (route.DestinationScheme == CaddyProxyTargetScheme.Https)
        {
            builder.AppendLine($"    reverse_proxy {targetUrl} {{");
            builder.AppendLine("        transport http {");
            builder.AppendLine("            tls_insecure_skip_verify");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
        }
        else
        {
            builder.AppendLine($"    reverse_proxy {targetUrl}");
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string EnsureManagedImport(string currentText, string importPath)
    {
        var currentWithoutPackagedDefault = CaddyMainConfigSanitizer.RemovePackagedDefaultSite(currentText ?? string.Empty);
        var normalizedCurrentText = string.IsNullOrWhiteSpace(currentWithoutPackagedDefault)
            ? string.Empty
            : currentWithoutPackagedDefault.TrimEnd() + Environment.NewLine;
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

    private async Task ReloadCaddyServiceAsync(CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "systemctl",
                ["reload", ServiceName],
                true,
                TimeSpan.FromMinutes(1),
                "Reload Caddy after writing LMS routes"),
            dryRun: false,
            cancellationToken);

        if (result.ExitCode == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Caddy config was written, but Caddy did not reload: {FirstCaddyProblemLine(result.StandardError, result.StandardOutput) ?? $"exit code {result.ExitCode}"}");
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
            entity.UpdatedAtUtc,
            (CaddyProxyRouteKind)entity.RouteKind,
            entity.SourceIp,
            entity.SourcePort,
            entity.DestinationIp,
            entity.DestinationPort,
            (CaddyProxyTargetScheme)entity.DestinationScheme);

    private static CaddyProxyRouteDefinition? MapOrNull(CaddyProxyRouteEntity? entity) =>
        entity is null ? null : Map(entity);

    private static CaddyProxyRouteEntity Map(CaddyProxyRouteDefinition route) =>
        new()
        {
            Id = route.Id,
            RouteKind = (int)route.Kind,
            Name = route.Name,
            Hostname = route.Hostname,
            UpstreamUrl = route.UpstreamUrl,
            SourceIp = route.SourceIp,
            SourcePort = route.SourcePort,
            DestinationIp = route.DestinationIp,
            DestinationPort = route.DestinationPort,
            DestinationScheme = (int)route.DestinationScheme,
            Description = route.Description,
            EnableTls = route.EnableTls,
            CreatedAtUtc = route.CreatedAtUtc,
            UpdatedAtUtc = route.UpdatedAtUtc
        };

    private static void Apply(CaddyProxyRouteEntity entity, CaddyProxyRouteDefinition route)
    {
        entity.RouteKind = (int)route.Kind;
        entity.Name = route.Name;
        entity.Hostname = route.Hostname;
        entity.UpstreamUrl = route.UpstreamUrl;
        entity.SourceIp = route.SourceIp;
        entity.SourcePort = route.SourcePort;
        entity.DestinationIp = route.DestinationIp;
        entity.DestinationPort = route.DestinationPort;
        entity.DestinationScheme = (int)route.DestinationScheme;
        entity.Description = route.Description;
        entity.EnableTls = route.EnableTls;
        entity.CreatedAtUtc = route.CreatedAtUtc;
        entity.UpdatedAtUtc = route.UpdatedAtUtc;
    }

    private async Task<OperationLogEntry> CheckTcpEndpointAsync(
        string label,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var normalizedHost = NormalizeConnectHost(host);
        var normalizedPort = Math.Clamp(port, 1, 65535);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(normalizedHost, normalizedPort, timeout.Token);
            return BuildLog(OperationLogLevel.Success, $"{label}: {FormatEndpoint(normalizedHost, normalizedPort)}.");
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or IOException)
        {
            return BuildLog(
                OperationLogLevel.Error,
                $"{label} failed: {FormatEndpoint(normalizedHost, normalizedPort)} is not reachable.",
                standardError: exception.Message);
        }
    }

    private async Task<OperationLogEntry> CheckDestinationProtocolAsync(
        CaddyProxyRouteDefinition route,
        CancellationToken cancellationToken)
    {
        var configuredScheme = route.DestinationScheme == CaddyProxyTargetScheme.Https ? "https" : "http";
        var alternateScheme = configuredScheme == "https" ? "http" : "https";
        var configuredProbe = await ProbeHttpAsync(configuredScheme, route.DestinationIp, route.DestinationPort, cancellationToken);
        if (configuredProbe.Success)
        {
            return BuildLog(
                OperationLogLevel.Success,
                $"Destination responds over {configuredScheme.ToUpperInvariant()} at {configuredProbe.Url}.",
                standardOutput: configuredProbe.Summary);
        }

        var alternateProbe = await ProbeHttpAsync(alternateScheme, route.DestinationIp, route.DestinationPort, cancellationToken);
        if (alternateProbe.Success)
        {
            return BuildLog(
                OperationLogLevel.Error,
                $"Destination is {alternateScheme.ToUpperInvariant()}, but this route is configured for {configuredScheme.ToUpperInvariant()}. Edit the route destination scheme to {alternateScheme.ToUpperInvariant()}.",
                standardError: configuredProbe.Summary);
        }

        return BuildLog(
            OperationLogLevel.Error,
            $"Destination did not respond as HTTP or HTTPS at {route.DestinationIp}:{Math.Clamp(route.DestinationPort, 1, 65535)}.",
            standardError: $"Configured {configuredScheme}: {configuredProbe.Summary}{Environment.NewLine}Alternate {alternateScheme}: {alternateProbe.Summary}");
    }

    private static async Task<HttpProbeResult> ProbeHttpAsync(
        string scheme,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var normalizedHost = NormalizeConnectHost(host);
        var normalizedPort = Math.Clamp(port, 1, 65535);
        var url = $"{scheme}://{FormatHostForUri(normalizedHost)}:{normalizedPort}/";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return new HttpProbeResult(true, url, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return new HttpProbeResult(false, url, exception.Message);
        }
    }

    private async Task<OperationLogEntry> CheckListenerSnapshotAsync(
        CaddyProxyRouteDefinition route,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "ss",
                ["-H", "-ltn"],
                false,
                TimeSpan.FromSeconds(10),
                "Inspect listening TCP sockets")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            return new OperationLogEntry(
                result.CompletedAt,
                OperationLogLevel.Warning,
                "Could not inspect listening sockets with ss.",
                result.CommandText,
                result.ExitCode,
                result.StandardOutput,
                result.StandardError);
        }

        var expectedPort = Math.Clamp(route.SourcePort, 1, 65535);
        var expectedHosts = CaddyBindAddressFormatter.IsAnyAddressList(route.SourceIp)
            ? ["0.0.0.0", "[::]", "*"]
            : CaddyBindAddressFormatter.NormalizeMany(route.SourceIp).Select(FormatListenerAddress).ToArray();
        var listenerLines = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Contains($":{expectedPort} ", StringComparison.Ordinal) ||
                           line.EndsWith($":{expectedPort}", StringComparison.Ordinal))
            .ToArray();

        var hasExpectedListener = listenerLines.Any(line =>
            expectedHosts.Any(host => line.Contains($"{host}:{expectedPort}", StringComparison.OrdinalIgnoreCase) ||
                                      line.Contains($"*:{expectedPort}", StringComparison.OrdinalIgnoreCase)));

        return new OperationLogEntry(
            result.CompletedAt,
            hasExpectedListener ? OperationLogLevel.Success : OperationLogLevel.Error,
            hasExpectedListener
                ? $"Caddy has a listening socket for {CaddyBindAddressFormatter.FormatEndpointLabel(route.SourceIp, expectedPort)}."
                : $"No listening socket found for {CaddyBindAddressFormatter.FormatEndpointLabel(route.SourceIp, expectedPort)}.",
            result.CommandText,
            result.ExitCode,
            string.Join(Environment.NewLine, listenerLines),
            hasExpectedListener ? null : "Caddy did not expose the expected source bind/port in the current listener table.");
    }

    private static bool ManagedConfigurationContainsRoute(string managedConfigText, CaddyProxyRouteDefinition route) =>
        managedConfigText.Contains($"# Route: {SanitizeComment(route.Name)}", StringComparison.Ordinal) &&
        managedConfigText.Contains(route.Kind == CaddyProxyRouteKind.PortForward
            ? $"http://:{Math.Clamp(route.SourcePort, 1, 65535)}"
            : route.EnableTls ? route.Hostname : $"http://{route.Hostname}", StringComparison.Ordinal);

    private static string ResolveSourceProbeHost(string sourceIp)
    {
        var addresses = CaddyBindAddressFormatter.NormalizeMany(sourceIp);
        var first = addresses.FirstOrDefault() ?? "127.0.0.1";
        return CaddyBindAddressFormatter.IsAnyAddressList(first) ? "127.0.0.1" : first;
    }

    private static string NormalizeConnectHost(string host)
    {
        var normalized = (host ?? string.Empty).Trim().Trim('[', ']');
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Equals("*", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("::", StringComparison.OrdinalIgnoreCase))
        {
            return "127.0.0.1";
        }

        return normalized;
    }

    private static string FormatEndpoint(string host, int port) =>
        IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]:{port}"
            : $"{host}:{port}";

    private static string FormatListenerAddress(string host)
    {
        var normalized = (host ?? string.Empty).Trim().Trim('[', ']');
        return IPAddress.TryParse(normalized, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : normalized;
    }

    private static OperationLogEntry BuildLog(
        OperationLogLevel level,
        string message,
        string? commandText = null,
        int? exitCode = null,
        string? standardOutput = null,
        string? standardError = null) =>
        new(DateTimeOffset.Now, level, message, commandText, exitCode, standardOutput, standardError);

    private sealed record HttpProbeResult(bool Success, string Url, string Summary);

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

    private static string? FirstCaddyProblemLine(params string[] values)
    {
        var fallback = string.Empty;
        foreach (var line in values
                     .SelectMany(value => (value ?? string.Empty).Split('\n'))
                     .Select(static line => line.Trim())
                     .Where(static line => !string.IsNullOrWhiteSpace(line)))
        {
            if (TryFormatCaddyJsonLine(line, out var formattedLine, out var isInformational))
            {
                if (isInformational)
                {
                    fallback = string.IsNullOrWhiteSpace(fallback) ? formattedLine : fallback;
                    continue;
                }

                return formattedLine;
            }

            if (line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains(" error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains(" failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains(" invalid", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }

            fallback = string.IsNullOrWhiteSpace(fallback) ? line : fallback;
        }

        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    private static bool TryFormatCaddyJsonLine(string line, out string formattedLine, out bool isInformational)
    {
        formattedLine = line;
        isInformational = false;

        if (!line.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var level = root.TryGetProperty("level", out var levelProperty)
                ? levelProperty.GetString() ?? string.Empty
                : string.Empty;
            var message = root.TryGetProperty("msg", out var messageProperty)
                ? messageProperty.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(message))
            {
                return true;
            }

            formattedLine = string.IsNullOrWhiteSpace(level)
                ? message
                : $"{level}: {message}";
            isInformational = level.Equals("info", StringComparison.OrdinalIgnoreCase) ||
                              level.Equals("debug", StringComparison.OrdinalIgnoreCase);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string SanitizeComment(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string BuildPortForwardTargetUrl(CaddyProxyRouteDefinition route)
    {
        var scheme = route.DestinationScheme == CaddyProxyTargetScheme.Https ? "https" : "http";
        return $"{scheme}://{FormatHostForUri(route.DestinationIp)}:{Math.Clamp(route.DestinationPort, 1, 65535)}";
    }

    private static string FormatHostForUri(string host)
    {
        var normalized = (host ?? string.Empty).Trim();
        if (System.Net.IPAddress.TryParse(normalized, out var address) &&
            address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return $"[{normalized.Trim('[', ']')}]";
        }

        return normalized;
    }
}
