// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayClientRouteRegistration(
    string OwnerType,
    string OwnerId,
    string ServiceId,
    string DisplayName,
    string OriginHostname,
    string DomainName,
    string TargetHost,
    int TargetPort,
    string PreferredPath,
    HomeLabClientRoutingStrategy RoutingStrategy,
    HomeLabBasePathSupportMode BasePathSupport,
    bool StripPathPrefix,
    bool ForwardPathPrefix,
    bool UsePublicHostHeader,
    EdgeGatewayAuthMode AuthMode,
    Guid? ExistingRouteId = null);

public sealed record EdgeGatewayClientEndpoint(
    Guid RouteId,
    string Url,
    string Scheme,
    string Host,
    string PathBase,
    HomeLabClientRoutingStrategy RoutingMode);
