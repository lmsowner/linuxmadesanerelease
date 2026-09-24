// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.EdgeGateway;

public sealed record EdgeGatewayTemporaryIpApprovalConfiguration(
    IReadOnlyList<EdgeGatewayTemporaryIpApprovalRequest> Requests,
    IReadOnlyList<EdgeGatewayTemporaryIpApprovalGrant> Grants,
    DateTimeOffset UpdatedAtUtc)
{
    public static EdgeGatewayTemporaryIpApprovalConfiguration Empty { get; } = new([], [], DateTimeOffset.UtcNow);
}

public sealed record EdgeGatewayTemporaryIpApprovalRequest(
    Guid Id,
    Guid RouteId,
    string RouteName,
    string PublicHostname,
    string TargetPathPrefix,
    string SourceIp,
    string CountryCode,
    string UserAgent,
    string RequestedUrl,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? LastEmailSentUtc,
    int EmailSendCount,
    string ApprovalTokenHash,
    DateTimeOffset? ApprovalTokenExpiresAtUtc,
    DateTimeOffset? ApprovedUtc,
    string LastEmailStatus);

public sealed record EdgeGatewayTemporaryIpApprovalGrant(
    Guid Id,
    Guid RouteId,
    string RouteName,
    string PublicHostname,
    string TargetPathPrefix,
    string SourceIp,
    string CountryCode,
    string UserAgent,
    DateTimeOffset ApprovedUtc,
    DateTimeOffset LastSeenUtc,
    DateTimeOffset IdleExpiresAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record EdgeGatewayTemporaryIpApprovalCheckContext(
    string RequestedHost,
    string RequestedPath,
    string RequestedUrl,
    string SourceIp,
    string CountryCode,
    string UserAgent);

public sealed record EdgeGatewayTemporaryIpApprovalEvaluationResult(
    bool IsAllowed,
    string Reason,
    bool EmailAttempted = false,
    bool EmailSucceeded = false);

public sealed record EdgeGatewayTemporaryIpApprovalCompletionResult(
    bool Success,
    string Title,
    string Message,
    string SourceIp = "",
    string CountryCode = "",
    string RouteName = "",
    string PublicHostname = "",
    string ApprovedUrl = "",
    DateTimeOffset? IdleExpiresAtUtc = null,
    DateTimeOffset? ExpiresAtUtc = null);
