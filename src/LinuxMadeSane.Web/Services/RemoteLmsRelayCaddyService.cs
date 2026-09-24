// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.EdgeGateway;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using Microsoft.Extensions.DependencyInjection;

namespace LinuxMadeSane.Web.Services;

public sealed class RemoteLmsRelayCaddyService(
    IServiceScopeFactory scopeFactory,
    ILogger<RemoteLmsRelayCaddyService> logger)
{
    private const string ServiceName = "caddy";
    private const string MainConfigPath = "/etc/caddy/Caddyfile";
    private const string ManagedRootDirectory = "/etc/caddy/linuxmadesane";
    private const string ManagedConfigPath = "/etc/caddy/linuxmadesane/remote-lms-relays.caddy";
    private static readonly Regex RepeatedDashPattern = new("-+", RegexOptions.Compiled);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, RemoteLmsRelayRoute> routes = [];

    public async Task<RemoteLmsRelayPublishResult> PublishAsync(
        ManagedHost host,
        int localPort,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        var relayDomain = await ResolveRelayDomainAsync(cancellationToken);
        var hostname = BuildRelayHostname(host, relayDomain.DomainName, relayDomain.GatewayDomainName);
        var returnPath = RemoteLmsTunnelAccessService.NormalizeReturnUrl(path);
        var route = new RemoteLmsRelayRoute(host.Id, host.Name, hostname, localPort, DateTimeOffset.UtcNow);

        await gate.WaitAsync(cancellationToken);
        try
        {
            routes[host.Id] = route;
            await EnsureRemoteRelayFileExistsAsync(cancellationToken);
            await EnsureCaddyReadyAsync(cancellationToken);
            await EnsureEdgeGatewayListenerAsync(cancellationToken);
            await EnsureRemoteLmsCloudflareRouteAsync(relayDomain.DomainName, hostname, cancellationToken);
            await ApplyRoutesUnsafeAsync(cancellationToken);
        }
        catch
        {
            routes.Remove(host.Id);
            throw;
        }
        finally
        {
            gate.Release();
        }

        return new RemoteLmsRelayPublishResult(hostname, $"https://{hostname}{returnPath}");
    }

    public async Task RemoveAsync(Guid hostId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!routes.Remove(hostId))
            {
                return;
            }

            await ApplyRoutesUnsafeAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove the remote LMS relay route for host {HostId}.", hostId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (routes.Count == 0)
            {
                return;
            }

            routes.Clear();
            await ApplyRoutesUnsafeAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clear remote LMS relay routes.");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<EdgeGatewayCloudflareDomainOption> ResolveRelayDomainAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var edgeGatewayService = scope.ServiceProvider.GetRequiredService<IEdgeGatewayService>();
        var dashboard = await edgeGatewayService.GetDashboardAsync(cancellationToken);
        var relayDomain = dashboard.Cloudflare.Domains
            .Where(static item => !item.Paused &&
                                  item.RelayConfigured &&
                                  item.RelayUsesCloudflareTunnel &&
                                  !string.IsNullOrWhiteSpace(item.GatewayDomainName))
            .OrderByDescending(static item => item.IsSavedDefault)
            .ThenBy(static item => item.DomainName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return relayDomain ?? throw new InvalidOperationException(
            "Set up and enable an Edge Gateway relay namespace before connecting to a remote LMS host. The relay must have Cloudflare tunnel DNS configured.");
    }

    private async Task EnsureCaddyReadyAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var caddyIntegrationService = scope.ServiceProvider.GetRequiredService<ICaddyIntegrationService>();
        var dashboard = await caddyIntegrationService.GetDashboardAsync(cancellationToken);

        if (!dashboard.IsInstalled)
        {
            var install = await caddyIntegrationService.InstallAsync(cancellationToken);
            if (!install.Success)
            {
                throw new InvalidOperationException($"Caddy could not be installed: {install.Summary}");
            }

            dashboard = await caddyIntegrationService.GetDashboardAsync(cancellationToken);
        }

        if (!dashboard.IsServiceActive)
        {
            var restart = await caddyIntegrationService.RestartAsync(cancellationToken);
            if (!restart.Success)
            {
                throw new InvalidOperationException($"Caddy could not be started: {restart.Summary}");
            }
        }
    }

    private async Task EnsureEdgeGatewayListenerAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var edgeGatewayService = scope.ServiceProvider.GetRequiredService<IEdgeGatewayService>();
        var apply = await edgeGatewayService.ApplyCaddyConfigurationAsync(cancellationToken);
        if (!apply.Success)
        {
            throw new InvalidOperationException(
                $"The Edge Gateway Caddy listener could not be prepared: {apply.Summary}");
        }
    }

    private async Task EnsureRemoteLmsCloudflareRouteAsync(
        string domainName,
        string hostname,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var edgeGatewayService = scope.ServiceProvider.GetRequiredService<IEdgeGatewayService>();
        var result = await edgeGatewayService.ProvisionRemoteLmsRelayAsync(domainName, hostname, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException($"The remote LMS Cloudflare relay could not be prepared: {result.Summary}");
        }
    }

    private async Task ApplyRoutesUnsafeAsync(CancellationToken cancellationToken)
    {
        await EnsureRemoteRelayFileExistsAsync(cancellationToken);
        var previousText = await ReadTextOrDefaultAsync(ManagedConfigPath, cancellationToken);
        var nextText = BuildCaddyfile(routes.Values);

        await WriteTextAsync(ManagedConfigPath, nextText, cancellationToken);
        try
        {
            await ValidateCaddyAsync(cancellationToken);
            await ReloadCaddyAsync(cancellationToken);
        }
        catch
        {
            await WriteTextAsync(ManagedConfigPath, previousText, cancellationToken);
            try
            {
                await ReloadCaddyAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Caddy reload failed while restoring the previous remote LMS relay routes.");
            }

            throw;
        }
    }

    private static string BuildCaddyfile(IEnumerable<RemoteLmsRelayRoute> relayRoutes)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Generated by Linux Made Sane. Do not edit manually.");
        builder.AppendLine("# Remote LMS relay routes. Imported inside the Edge Gateway Caddy listener.");
        builder.AppendLine();

        foreach (var route in relayRoutes.OrderBy(static item => item.Hostname, StringComparer.OrdinalIgnoreCase))
        {
            var matcherName = $"remote_lms_{route.HostId:N}";
            var grantMatcherName = $"remote_lms_grants_{route.HostId:N}";
            builder.AppendLine($"@{grantMatcherName} {{");
            builder.AppendLine($"    host {route.Hostname}");
            builder.AppendLine("    path /internal/lms-tunnel/grants /internal/lms-tunnel/grants/*");
            builder.AppendLine("}");
            builder.AppendLine($"handle @{grantMatcherName} {{");
            builder.AppendLine("    respond 404");
            builder.AppendLine("}");
            builder.AppendLine();

            builder.AppendLine($"@{matcherName} host {route.Hostname}");
            builder.AppendLine($"handle @{matcherName} {{");
            builder.AppendLine($"    reverse_proxy http://127.0.0.1:{Math.Clamp(route.LocalPort, 1, 65535)} {{");
            builder.AppendLine("        header_up Host {host}");
            builder.AppendLine("        header_up X-Forwarded-Proto https");
            builder.AppendLine("        header_up X-Forwarded-Port 443");
            builder.AppendLine("        header_up X-Forwarded-For 127.0.0.1");
            builder.AppendLine("    }");
            builder.AppendLine("}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private async Task ValidateCaddyAsync(CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync(
            "caddy",
            ["validate", "--config", MainConfigPath, "--adapter", "caddyfile"],
            false,
            TimeSpan.FromSeconds(20),
            "Validate remote LMS relay Caddy configuration",
            true,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                FirstUsefulFailureLine(result.StandardError, result.StandardOutput) ??
                "Caddy rejected the remote LMS relay configuration.");
        }
    }

    private async Task ReloadCaddyAsync(CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync(
            "systemctl",
            ["reload", ServiceName],
            true,
            TimeSpan.FromMinutes(1),
            "Reload Caddy after remote LMS relay update",
            false,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                FirstUsefulFailureLine(result.StandardError, result.StandardOutput) ??
                "Caddy reload failed after updating remote LMS relay routes.");
        }
    }

    private async Task EnsureRemoteRelayFileExistsAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(ManagedConfigPath))
        {
            return;
        }

        await EnsureDirectoryAsync(ManagedRootDirectory, cancellationToken);
        await WriteTextAsync(ManagedConfigPath, BuildCaddyfile([]), cancellationToken);
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

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"lms-remote-relay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryFilePath = Path.Combine(temporaryDirectory, Path.GetRandomFileName());
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
            if (File.Exists(temporaryFilePath))
            {
                File.Delete(temporaryFilePath);
            }

            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private async Task RunRequiredCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync(
            fileName,
            arguments,
            true,
            TimeSpan.FromMinutes(1),
            description,
            false,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{description} failed: {FirstUsefulFailureLine(result.StandardError, result.StandardOutput) ?? $"exit code {result.ExitCode}"}");
        }
    }

    private async Task<LinuxCommandResult> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        bool useSudo,
        TimeSpan timeout,
        string description,
        bool optionalExternalTool,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var commandRunner = scope.ServiceProvider.GetRequiredService<ILinuxCommandRunner>();
        return await commandRunner.RunAsync(
            new LinuxCommandRequest(fileName, arguments, useSudo, timeout, description)
            {
                IsOptionalExternalTool = optionalExternalTool
            },
            dryRun: false,
            cancellationToken);
    }

    private static async Task<string> ReadTextOrDefaultAsync(string path, CancellationToken cancellationToken) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : string.Empty;

    private static string BuildRelayHostname(ManagedHost host, string domainName, string gatewayDomainName)
    {
        var zoneDomain = NormalizeHostname(domainName);
        var relayLabel = BuildGatewayRelayLabel(gatewayDomainName, zoneDomain);
        var slug = BuildHostSlug(host);
        var shortId = host.Id.ToString("N")[..8];
        return NormalizeHostname($"{slug}-{shortId}-lms-{relayLabel}.{zoneDomain}");
    }

    private static string BuildGatewayRelayLabel(string gatewayDomainName, string zoneDomain)
    {
        var gatewayDomain = NormalizeHostname(gatewayDomainName);
        var suffix = $".{zoneDomain}";
        var label = gatewayDomain.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? gatewayDomain[..^suffix.Length]
            : gatewayDomain;
        label = label.Replace('.', '-');
        return BuildDnsLabelSlug(label, "relay", maxLength: 20);
    }

    private static string BuildHostSlug(ManagedHost host)
    {
        var value = string.IsNullOrWhiteSpace(host.Name) ? host.Hostname : host.Name;
        value = string.IsNullOrWhiteSpace(value) ? "remote-lms" : value;
        return BuildDnsLabelSlug(value, "remote-lms", maxLength: 24);
    }

    private static string BuildDnsLabelSlug(string value, string fallback, int maxLength)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) ? character : '-');
        }

        var slug = RepeatedDashPattern.Replace(builder.ToString(), "-").Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = fallback;
        }

        return slug.Length > maxLength ? slug[..maxLength].Trim('-') : slug;
    }

    private static string NormalizeHostname(string value)
    {
        var normalized = (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.Length == 0 ||
            normalized.Length > 253 ||
            normalized.Contains("://", StringComparison.Ordinal) ||
            normalized.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The configured Edge Gateway relay namespace is not a valid hostname.");
        }

        var labels = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2 || labels.Any(static label => label.Length == 0 || label.Length > 63))
        {
            throw new InvalidOperationException("The configured Edge Gateway relay namespace is not a valid hostname.");
        }

        return normalized;
    }

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

        return lines.FirstOrDefault(static line =>
                   !line.Contains("\"level\":\"info\"", StringComparison.OrdinalIgnoreCase) &&
                   !line.Contains("\"level\":\"warn\"", StringComparison.OrdinalIgnoreCase)) ??
               lines[0];
    }

    private sealed record RemoteLmsRelayRoute(
        Guid HostId,
        string HostName,
        string Hostname,
        int LocalPort,
        DateTimeOffset CreatedAtUtc);
}

public sealed record RemoteLmsRelayPublishResult(string Hostname, string Url);
