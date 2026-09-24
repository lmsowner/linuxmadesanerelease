// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.HomeLab;

public sealed record HomeLabPortManifest(
    string Name,
    int ContainerPort,
    string Protocol = "tcp",
    bool Primary = false,
    int? VpnContainerPort = null,
    string? VpnEnvironmentVariable = null,
    HomeLabVpnPortFileOverride? VpnFileOverride = null,
    string ApplicationProtocol = "");

public sealed record HomeLabVpnPortFileOverride(
    string Path,
    string Setting,
    string OriginalEntrypoint);

public sealed record HomeLabVolumeManifest(
    string Id,
    string ContainerPath,
    HomeLabStorageKind Kind,
    string? SharedRole = null,
    bool ReadOnly = false,
    string? HostOwner = null);

public sealed record HomeLabHealthCheckManifest(
    string? HttpPath = null,
    int? Port = null,
    string? DockerCommand = null,
    int StartPeriodSeconds = 20,
    int IntervalSeconds = 30,
    int TimeoutSeconds = 5,
    int Retries = 3);

public sealed record HomeLabConfigurationField(
    string Id,
    string Label,
    string Type = "text",
    bool Required = false,
    bool Secret = false,
    IReadOnlyList<string>? Options = null,
    string? Help = null);

public sealed record HomeLabEndpointCapabilities(
    bool WebSockets = false,
    bool ServerSentEvents = false,
    bool Streaming = false,
    bool RangeRequests = false,
    bool Uploads = false,
    bool LongLivedRequests = false,
    string? WebSocketProbePath = null,
    string? ServerSentEventsProbePath = null,
    string? RangeRequestProbePath = null);

public sealed record HomeLabReverseProxyManifest(
    HomeLabBasePathSupportMode BasePathSupport = HomeLabBasePathSupportMode.None,
    bool StripPathPrefix = true,
    bool UsePublicHostHeader = true,
    bool ForwardPathPrefix = true,
    bool RewriteSecureCookiesForHttp = false,
    HomeLabBasePathConfiguration? Configuration = null);

public sealed record HomeLabBasePathConfiguration(
    string? EnvironmentVariable = null,
    string? FilePath = null,
    string? XmlElement = null,
    string ValueTemplate = "${endpoint:self:client:path}");

public sealed record HomeLabClientAccessManifest(
    bool Enabled,
    string PortName = "web",
    HomeLabClientRoutingStrategy Routing = HomeLabClientRoutingStrategy.Auto,
    string PreferredPath = "",
    HomeLabReverseProxyManifest? ReverseProxy = null,
    HomeLabEndpointCapabilities? Capabilities = null,
    string? PublicUrlEnvironmentVariable = null);

public sealed record HomeLabServiceExposureManifest(
    IReadOnlyList<HomeLabEndpointScope> Scopes,
    HomeLabClientAccessManifest? ClientAccess = null,
    IReadOnlyList<HomeLabClientAccessManifest>? AdditionalClientAccess = null);

public sealed record HomeLabRecipeDependency(
    string Consumer,
    string Provider,
    HomeLabDependencyAccessFrom AccessFrom = HomeLabDependencyAccessFrom.Service,
    string? Purpose = null,
    string? EndpointReference = null);

public sealed record HomeLabAppManifest(
    string Id,
    string Name,
    string Description,
    HomeLabAppCategory Category,
    string Icon,
    string Website,
    string Documentation,
    string ImageRepository,
    string ImageTag,
    string DefinitionVersion,
    IReadOnlyList<HomeLabPortManifest> Ports,
    IReadOnlyList<HomeLabVolumeManifest> Volumes,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> Dependencies,
    HomeLabHealthCheckManifest? HealthCheck,
    IReadOnlyList<HomeLabConfigurationField> ConfigurationSchema,
    bool SupportsDirectNetwork = true,
    bool SupportsVpnGateway = false,
    bool RequiresVpnGateway = false,
    bool EdgeGatewaySupported = true,
    bool IsInstallable = true,
    IReadOnlyList<string>? DockerCapabilities = null,
    IReadOnlyList<string>? DockerDevices = null,
    bool IsSystemDependency = false,
    string? PublicUrlEnvironmentVariable = null,
    HomeLabServiceExposureManifest? Exposure = null);

public sealed record HomeLabRecipeRelationship(
    string AppId,
    string? RouteVia = null,
    string? DownloadClient = null,
    string? IndexerManager = null,
    string? Sonarr = null,
    string? Radarr = null);

public sealed record HomeLabRecipeManifest(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> AppIds,
    IReadOnlyList<string> SharedStorageRoles,
    IReadOnlyList<HomeLabRecipeRelationship> Relationships,
    bool RequiresVpnGateway = false,
    bool IsInstallable = true,
    IReadOnlyList<HomeLabRecipeDependency>? ConnectivityDependencies = null);
