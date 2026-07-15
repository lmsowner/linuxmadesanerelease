// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayRouteListItem(
    Guid Id,
    bool Enabled,
    string DisplayName,
    string Hostname,
    string DomainName,
    string TargetPathPrefix,
    string TargetUrl,
    EdgeGatewayAuthMode AuthMode,
    bool UsePublicHostHeader,
    bool StripForwardedFor,
    bool SkipUpstreamTlsVerification,
    string TemporaryIpApprovalRecipients,
    string TemporaryIpApprovalAllowedCountryCodes,
    bool TemporaryIpApprovalUseNotFoundResponse,
    int? TemporaryIpApprovalIdleTimeoutMinutes,
    int? TemporaryIpApprovalMaxLifetimeMinutes,
    EdgeGatewayDiagnosticStatus LastTestStatus,
    string LastTestMessage,
    DateTimeOffset UpdatedAt);
