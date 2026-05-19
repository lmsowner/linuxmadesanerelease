// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.EdgeGateway;

public sealed record EdgeGatewayRoute(
    Guid Id,
    bool Enabled,
    string DisplayName,
    string Hostname,
    string DomainName,
    EdgeGatewayTargetScheme TargetScheme,
    string TargetHost,
    int TargetPort,
    string TargetPathPrefix,
    EdgeGatewayAuthMode AuthMode,
    string AllowedUsers,
    string AllowedGroups,
    bool AllowLanOnly,
    string AllowKnownIps,
    string Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    EdgeGatewayDiagnosticStatus LastTestStatus,
    string LastTestMessage);
