// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.EdgeGateway;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteEdgeGatewayStore(LinuxMadeSaneDbContext dbContext) : IEdgeGatewayStore
{
    public async Task<IReadOnlyList<EdgeGatewayRoute>> ListRoutesAsync(CancellationToken cancellationToken = default) =>
        await dbContext.EdgeGatewayRoutes
            .AsNoTracking()
            .OrderBy(route => route.Hostname)
            .Select(route => Map(route))
            .ToArrayAsync(cancellationToken);

    public async Task<EdgeGatewayRoute?> GetRouteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.EdgeGatewayRoutes
            .AsNoTracking()
            .SingleOrDefaultAsync(route => route.Id == id, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<EdgeGatewayRoute?> FindRouteByHostnameAsync(string hostname, CancellationToken cancellationToken = default)
    {
        var normalizedHostname = hostname.Trim().TrimEnd('.').ToLowerInvariant();
        var entities = await dbContext.EdgeGatewayRoutes
            .AsNoTracking()
            .Where(route => route.Hostname == normalizedHostname)
            .ToListAsync(cancellationToken);

        return entities
            .OrderBy(static route => route.TargetPathPrefix.Length)
            .Select(Map)
            .FirstOrDefault();
    }

    public async Task SaveRouteAsync(EdgeGatewayRoute route, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.EdgeGatewayRoutes
            .SingleOrDefaultAsync(existing => existing.Id == route.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.EdgeGatewayRoutes.Add(Map(route));
        }
        else
        {
            Apply(entity, route);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRouteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.EdgeGatewayRoutes
            .SingleOrDefaultAsync(route => route.Id == id, cancellationToken);

        if (entity is null)
        {
            return;
        }

        dbContext.EdgeGatewayRoutes.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DisableAllRoutesAsync(DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        var routes = await dbContext.EdgeGatewayRoutes.ToListAsync(cancellationToken);
        foreach (var route in routes)
        {
            route.Enabled = false;
            route.UpdatedAt = updatedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddAuditEntryAsync(EdgeGatewayAuditEntry entry, CancellationToken cancellationToken = default)
    {
        dbContext.EdgeGatewayAuditEntries.Add(new EdgeGatewayAuditEntryEntity
        {
            Id = entry.Id,
            TimestampUtc = entry.TimestampUtc,
            Hostname = entry.Hostname,
            RouteId = entry.RouteId,
            RequestedPath = entry.RequestedPath,
            SourceIp = entry.SourceIp,
            UserEmail = entry.UserEmail,
            Decision = (int)entry.Decision,
            Reason = entry.Reason,
            AuthMode = (int)entry.AuthMode
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EdgeGatewayAuditEntry>> ListAuditEntriesAsync(
        string? hostname = null,
        string? userEmail = null,
        string? decision = null,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        int take = 250,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.EdgeGatewayAuditEntries.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(hostname))
        {
            var normalizedHostname = hostname.Trim().ToLowerInvariant();
            query = query.Where(entry => entry.Hostname.Contains(normalizedHostname));
        }

        if (!string.IsNullOrWhiteSpace(userEmail))
        {
            var normalizedEmail = userEmail.Trim().ToLowerInvariant();
            query = query.Where(entry => entry.UserEmail.ToLower().Contains(normalizedEmail));
        }

        if (!string.IsNullOrWhiteSpace(decision) &&
            Enum.TryParse<EdgeGatewayDecision>(decision.Trim(), true, out var parsedDecision))
        {
            query = query.Where(entry => entry.Decision == (int)parsedDecision);
        }

        var entries = await query.ToListAsync(cancellationToken);

        if (fromUtc.HasValue)
        {
            entries = entries
                .Where(entry => entry.TimestampUtc >= fromUtc.Value)
                .ToList();
        }

        if (toUtc.HasValue)
        {
            entries = entries
                .Where(entry => entry.TimestampUtc <= toUtc.Value)
                .ToList();
        }

        return entries
            .OrderByDescending(entry => entry.TimestampUtc)
            .Take(Math.Clamp(take, 1, 1000))
            .Select(Map)
            .ToArray();
    }

    private static EdgeGatewayRoute Map(EdgeGatewayRouteEntity entity) =>
        new(
            entity.Id,
            entity.Enabled,
            entity.DisplayName,
            entity.Hostname,
            entity.DomainName,
            Enum.IsDefined(typeof(EdgeGatewayTargetScheme), entity.TargetScheme)
                ? (EdgeGatewayTargetScheme)entity.TargetScheme
                : EdgeGatewayTargetScheme.Http,
            entity.TargetHost,
            entity.TargetPort,
            entity.TargetPathPrefix,
            Enum.IsDefined(typeof(EdgeGatewayAuthMode), entity.AuthMode)
                ? (EdgeGatewayAuthMode)entity.AuthMode
                : EdgeGatewayAuthMode.RequireMfa,
            entity.AllowedUsers,
            entity.AllowedGroups,
            entity.AllowLanOnly,
            entity.AllowKnownIps,
            entity.Notes,
            entity.CreatedAt,
            entity.UpdatedAt,
            Enum.IsDefined(typeof(EdgeGatewayDiagnosticStatus), entity.LastTestStatus)
                ? (EdgeGatewayDiagnosticStatus)entity.LastTestStatus
                : EdgeGatewayDiagnosticStatus.NotConfigured,
            entity.LastTestMessage);

    private static EdgeGatewayRouteEntity Map(EdgeGatewayRoute route) =>
        new()
        {
            Id = route.Id,
            Enabled = route.Enabled,
            DisplayName = route.DisplayName,
            Hostname = route.Hostname,
            DomainName = route.DomainName,
            TargetScheme = (int)route.TargetScheme,
            TargetHost = route.TargetHost,
            TargetPort = route.TargetPort,
            TargetPathPrefix = route.TargetPathPrefix,
            AuthMode = (int)route.AuthMode,
            AllowedUsers = route.AllowedUsers,
            AllowedGroups = route.AllowedGroups,
            AllowLanOnly = route.AllowLanOnly,
            AllowKnownIps = route.AllowKnownIps,
            Notes = route.Notes,
            CreatedAt = route.CreatedAt,
            UpdatedAt = route.UpdatedAt,
            LastTestStatus = (int)route.LastTestStatus,
            LastTestMessage = route.LastTestMessage
        };

    private static void Apply(EdgeGatewayRouteEntity entity, EdgeGatewayRoute route)
    {
        entity.Enabled = route.Enabled;
        entity.DisplayName = route.DisplayName;
        entity.Hostname = route.Hostname;
        entity.DomainName = route.DomainName;
        entity.TargetScheme = (int)route.TargetScheme;
        entity.TargetHost = route.TargetHost;
        entity.TargetPort = route.TargetPort;
        entity.TargetPathPrefix = route.TargetPathPrefix;
        entity.AuthMode = (int)route.AuthMode;
        entity.AllowedUsers = route.AllowedUsers;
        entity.AllowedGroups = route.AllowedGroups;
        entity.AllowLanOnly = route.AllowLanOnly;
        entity.AllowKnownIps = route.AllowKnownIps;
        entity.Notes = route.Notes;
        entity.CreatedAt = route.CreatedAt;
        entity.UpdatedAt = route.UpdatedAt;
        entity.LastTestStatus = (int)route.LastTestStatus;
        entity.LastTestMessage = route.LastTestMessage;
    }

    private static EdgeGatewayAuditEntry Map(EdgeGatewayAuditEntryEntity entity) =>
        new(
            entity.Id,
            entity.TimestampUtc,
            entity.Hostname,
            entity.RouteId,
            entity.RequestedPath,
            entity.SourceIp,
            entity.UserEmail,
            Enum.IsDefined(typeof(EdgeGatewayDecision), entity.Decision)
                ? (EdgeGatewayDecision)entity.Decision
                : EdgeGatewayDecision.Denied,
            entity.Reason,
            Enum.IsDefined(typeof(EdgeGatewayAuthMode), entity.AuthMode)
                ? (EdgeGatewayAuthMode)entity.AuthMode
                : EdgeGatewayAuthMode.Blocked);
}
