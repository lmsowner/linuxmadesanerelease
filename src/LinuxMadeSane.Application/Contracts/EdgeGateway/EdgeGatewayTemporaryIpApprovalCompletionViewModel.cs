// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayTemporaryIpApprovalCompletionViewModel(
    bool Success,
    string Title,
    string Message,
    string SourceIp,
    string CountryCode,
    string RouteName,
    string PublicHostname,
    string ApprovedUrl,
    DateTimeOffset? IdleExpiresAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    bool IsBlocked = false);
