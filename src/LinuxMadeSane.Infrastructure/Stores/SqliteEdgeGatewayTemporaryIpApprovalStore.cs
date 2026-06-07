// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.EdgeGateway;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteEdgeGatewayTemporaryIpApprovalStore(LinuxMadeSaneDbContext dbContext)
    : IEdgeGatewayTemporaryIpApprovalStore
{
    public async Task<EdgeGatewayTemporaryIpApprovalConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        var requests = await dbContext.EdgeGatewayTemporaryIpApprovalRequests
            .AsNoTracking()
            .Select(request => Map(request))
            .ToArrayAsync(cancellationToken);
        var grants = await dbContext.EdgeGatewayTemporaryIpApprovalGrants
            .AsNoTracking()
            .Select(grant => Map(grant))
            .ToArrayAsync(cancellationToken);

        var updatedAtUtc = requests.Select(static request => request.UpdatedUtc)
            .Concat(grants.Select(static grant => grant.LastSeenUtc))
            .DefaultIfEmpty(DateTimeOffset.UtcNow)
            .Max();
        return Normalize(new EdgeGatewayTemporaryIpApprovalConfiguration(requests, grants, updatedAtUtc));
    }

    public async Task SaveAsync(
        EdgeGatewayTemporaryIpApprovalConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(configuration);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var existingRequests = await dbContext.EdgeGatewayTemporaryIpApprovalRequests.ToListAsync(cancellationToken);
        var existingGrants = await dbContext.EdgeGatewayTemporaryIpApprovalGrants.ToListAsync(cancellationToken);
        dbContext.EdgeGatewayTemporaryIpApprovalRequests.RemoveRange(existingRequests);
        dbContext.EdgeGatewayTemporaryIpApprovalGrants.RemoveRange(existingGrants);
        await dbContext.SaveChangesAsync(cancellationToken);

        dbContext.EdgeGatewayTemporaryIpApprovalRequests.AddRange(normalized.Requests.Select(Map));
        dbContext.EdgeGatewayTemporaryIpApprovalGrants.AddRange(normalized.Grants.Select(Map));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static EdgeGatewayTemporaryIpApprovalConfiguration Normalize(
        EdgeGatewayTemporaryIpApprovalConfiguration? configuration)
    {
        if (configuration is null)
        {
            return EdgeGatewayTemporaryIpApprovalConfiguration.Empty;
        }

        return configuration with
        {
            Requests = configuration.Requests?
                .Where(request => request.Id != Guid.Empty &&
                                  request.RouteId != Guid.Empty &&
                                  !string.IsNullOrWhiteSpace(request.SourceIp))
                .Select(request => request with
                {
                    RouteName = request.RouteName?.Trim() ?? string.Empty,
                    PublicHostname = NormalizeHostname(request.PublicHostname),
                    TargetPathPrefix = request.TargetPathPrefix?.Trim() ?? string.Empty,
                    SourceIp = request.SourceIp?.Trim() ?? string.Empty,
                    CountryCode = NormalizeCountryCode(request.CountryCode),
                    UserAgent = request.UserAgent?.Trim() ?? string.Empty,
                    RequestedUrl = request.RequestedUrl?.Trim() ?? string.Empty,
                    ApprovalTokenHash = request.ApprovalTokenHash ?? string.Empty,
                    LastEmailStatus = request.LastEmailStatus ?? string.Empty
                })
                .ToArray() ?? [],
            Grants = configuration.Grants?
                .Where(grant => grant.Id != Guid.Empty &&
                                grant.RouteId != Guid.Empty &&
                                !string.IsNullOrWhiteSpace(grant.SourceIp))
                .Select(grant => grant with
                {
                    RouteName = grant.RouteName?.Trim() ?? string.Empty,
                    PublicHostname = NormalizeHostname(grant.PublicHostname),
                    TargetPathPrefix = grant.TargetPathPrefix?.Trim() ?? string.Empty,
                    SourceIp = grant.SourceIp?.Trim() ?? string.Empty,
                    CountryCode = NormalizeCountryCode(grant.CountryCode),
                    UserAgent = grant.UserAgent?.Trim() ?? string.Empty
                })
                .ToArray() ?? []
        };
    }

    private static EdgeGatewayTemporaryIpApprovalRequest Map(EdgeGatewayTemporaryIpApprovalRequestEntity entity) =>
        new(
            entity.Id,
            entity.RouteId,
            entity.RouteName,
            entity.PublicHostname,
            entity.TargetPathPrefix,
            entity.SourceIp,
            entity.CountryCode,
            entity.UserAgent,
            entity.RequestedUrl,
            entity.CreatedUtc,
            entity.UpdatedUtc,
            entity.LastEmailSentUtc,
            entity.EmailSendCount,
            entity.ApprovalTokenHash,
            entity.ApprovalTokenExpiresAtUtc,
            entity.ApprovedUtc,
            entity.LastEmailStatus);

    private static EdgeGatewayTemporaryIpApprovalRequestEntity Map(EdgeGatewayTemporaryIpApprovalRequest request) =>
        new()
        {
            Id = request.Id,
            RouteId = request.RouteId,
            RouteName = request.RouteName,
            PublicHostname = request.PublicHostname,
            TargetPathPrefix = request.TargetPathPrefix,
            SourceIp = request.SourceIp,
            CountryCode = request.CountryCode,
            UserAgent = request.UserAgent,
            RequestedUrl = request.RequestedUrl,
            CreatedUtc = request.CreatedUtc,
            UpdatedUtc = request.UpdatedUtc,
            LastEmailSentUtc = request.LastEmailSentUtc,
            EmailSendCount = request.EmailSendCount,
            ApprovalTokenHash = request.ApprovalTokenHash,
            ApprovalTokenExpiresAtUtc = request.ApprovalTokenExpiresAtUtc,
            ApprovedUtc = request.ApprovedUtc,
            LastEmailStatus = request.LastEmailStatus
        };

    private static EdgeGatewayTemporaryIpApprovalGrant Map(EdgeGatewayTemporaryIpApprovalGrantEntity entity) =>
        new(
            entity.Id,
            entity.RouteId,
            entity.RouteName,
            entity.PublicHostname,
            entity.TargetPathPrefix,
            entity.SourceIp,
            entity.CountryCode,
            entity.UserAgent,
            entity.ApprovedUtc,
            entity.LastSeenUtc,
            entity.IdleExpiresAtUtc,
            entity.ExpiresAtUtc);

    private static EdgeGatewayTemporaryIpApprovalGrantEntity Map(EdgeGatewayTemporaryIpApprovalGrant grant) =>
        new()
        {
            Id = grant.Id,
            RouteId = grant.RouteId,
            RouteName = grant.RouteName,
            PublicHostname = grant.PublicHostname,
            TargetPathPrefix = grant.TargetPathPrefix,
            SourceIp = grant.SourceIp,
            CountryCode = grant.CountryCode,
            UserAgent = grant.UserAgent,
            ApprovedUtc = grant.ApprovedUtc,
            LastSeenUtc = grant.LastSeenUtc,
            IdleExpiresAtUtc = grant.IdleExpiresAtUtc,
            ExpiresAtUtc = grant.ExpiresAtUtc
        };

    private static string NormalizeHostname(string? value) =>
        (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    private static string NormalizeCountryCode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return normalized.Length == 2 ? normalized : string.Empty;
    }
}
