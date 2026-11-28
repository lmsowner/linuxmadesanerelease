// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayRouteListItem(
    Guid Id,
    bool Enabled,
    string DisplayName,
    string Hostname,
    string DomainName,
    string TargetPathPrefix,
    bool StripPathPrefix,
    bool ForwardPathPrefix,
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
