// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.HomeLab;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.HomeLab;
using LinuxMadeSane.Infrastructure.Persistence.Entities;

namespace LinuxMadeSane.Infrastructure.Services;

internal static partial class HomeLabEndpointPlanner
{
    public static HomeLabClientAccessManifest? ResolveClientAccess(HomeLabAppManifest app)
    {
        return ResolveClientAccesses(app).FirstOrDefault();
    }

    public static IReadOnlyList<HomeLabClientAccessManifest> ResolveClientAccesses(HomeLabAppManifest app)
    {
        if (app.Exposure is not null)
        {
            if (!app.Exposure.Scopes.Contains(HomeLabEndpointScope.Client))
            {
                return [];
            }

            return (app.Exposure.ClientAccess is { Enabled: true } primaryAccess
                    ? new[] { primaryAccess }
                    : Array.Empty<HomeLabClientAccessManifest>())
                .Concat(app.Exposure.AdditionalClientAccess ?? [])
                .Where(access => access.Enabled)
                .GroupBy(access => access.PortName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
        }

        var primary = app.Ports.FirstOrDefault(port => port.Primary) ?? app.Ports.FirstOrDefault();
        return primary?.Name.Equals("web", StringComparison.OrdinalIgnoreCase) == true
            ? [new HomeLabClientAccessManifest(
                true,
                primary.Name,
                HomeLabClientRoutingStrategy.Auto,
                $"/{app.Id}",
                new HomeLabReverseProxyManifest(HomeLabBasePathSupportMode.None))]
            : [];
    }

    public static bool IncludesScope(HomeLabAppManifest app, HomeLabEndpointScope scope)
    {
        if (app.Exposure is not null)
        {
            return app.Exposure.Scopes.Contains(scope);
        }

        return scope == HomeLabEndpointScope.Internal ||
               ResolveClientAccess(app) is not null && scope is HomeLabEndpointScope.Lan or HomeLabEndpointScope.Client;
    }

    public static HomeLabClientRoutingStrategy ResolvePublicRouting(HomeLabClientAccessManifest access) =>
        access.Routing switch
        {
            HomeLabClientRoutingStrategy.Subpath when access.ReverseProxy?.BasePathSupport == HomeLabBasePathSupportMode.None =>
                throw new InvalidOperationException("This service declares no reverse-proxy base-path support and cannot use subpath routing."),
            HomeLabClientRoutingStrategy.Auto when access.ReverseProxy?.BasePathSupport == HomeLabBasePathSupportMode.None =>
                HomeLabClientRoutingStrategy.Subdomain,
            HomeLabClientRoutingStrategy.Auto => HomeLabClientRoutingStrategy.Subpath,
            _ => access.Routing
        };

    public static string NormalizePreferredPath(string serviceId, string? preferredPath)
    {
        var candidate = string.IsNullOrWhiteSpace(preferredPath) ? $"/{serviceId}" : preferredPath.Trim();
        if (!candidate.StartsWith("/", StringComparison.Ordinal))
        {
            candidate = $"/{candidate}";
        }

        candidate = candidate.TrimEnd('/');
        if (candidate.Length < 2 || candidate.Contains("..", StringComparison.Ordinal) || candidate.Contains('\\'))
        {
            throw new InvalidOperationException($"The preferred client path for '{serviceId}' is invalid.");
        }

        return candidate;
    }

    public static string BuildRecipePortPath(int hostPort)
    {
        if (hostPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("A published Home Lab service must have a valid host port before it can be exposed through a recipe origin.");
        }

        return $"/{hostPort}";
    }

    public static string ResolveInternalHost(HomeLabInstallationEntity installation) => installation.AppId;

    public static int ResolveInternalPort(HomeLabPortManifest port, HomeLabInstallationEntity installation) =>
        HomeLabContainerPortPlan.Resolve(
            port,
            installation.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase));

    public static string RenderEndpointTokens(
        string value,
        string currentServiceId,
        Func<string, HomeLabEndpointScope, HomeLabServiceEndpoint?> resolve)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("${endpoint:", StringComparison.Ordinal))
        {
            return value;
        }

        return EndpointTokenPattern().Replace(value, match =>
        {
            var serviceId = match.Groups["service"].Value.Equals("self", StringComparison.OrdinalIgnoreCase)
                ? currentServiceId
                : match.Groups["service"].Value;
            if (!Enum.TryParse<HomeLabEndpointScope>(match.Groups["scope"].Value, true, out var scope))
            {
                throw new InvalidOperationException($"Endpoint token '{match.Value}' uses an unknown scope.");
            }

            var endpoint = resolve(serviceId, scope)
                ?? throw new InvalidOperationException($"Endpoint token '{match.Value}' could not be resolved for this deployment.");
            return match.Groups["part"].Value.ToLowerInvariant() switch
            {
                "" or "url" => endpoint.Url,
                "scheme" => endpoint.Scheme,
                "host" => endpoint.Host,
                "port" => endpoint.Port?.ToString() ?? string.Empty,
                "path" => endpoint.PathBase,
                _ => throw new InvalidOperationException($"Endpoint token '{match.Value}' requests an unknown endpoint field.")
            };
        });
    }

    [GeneratedRegex("\\$\\{endpoint:(?<service>[a-zA-Z0-9._-]+):(?<scope>internal|lan|client|public)(?::(?<part>url|scheme|host|port|path))?\\}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EndpointTokenPattern();
}
