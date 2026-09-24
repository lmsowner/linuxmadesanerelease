// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
    DateTimeOffset? ExpiresAtUtc);
