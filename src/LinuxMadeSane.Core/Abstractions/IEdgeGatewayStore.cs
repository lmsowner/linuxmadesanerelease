// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.EdgeGateway;

namespace LinuxMadeSane.Core.Abstractions;

public interface IEdgeGatewayStore
{
    Task<IReadOnlyList<EdgeGatewayRoute>> ListRoutesAsync(CancellationToken cancellationToken = default);
    Task<EdgeGatewayRoute?> GetRouteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<EdgeGatewayRoute?> FindRouteByHostnameAsync(string hostname, CancellationToken cancellationToken = default);
    Task SaveRouteAsync(EdgeGatewayRoute route, CancellationToken cancellationToken = default);
    Task DeleteRouteAsync(Guid id, CancellationToken cancellationToken = default);
    Task DisableAllRoutesAsync(DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);
    Task AddAuditEntryAsync(EdgeGatewayAuditEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EdgeGatewayAuditEntry>> ListAuditEntriesAsync(
        string? hostname = null,
        string? userEmail = null,
        string? decision = null,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        int take = 250,
        CancellationToken cancellationToken = default);
}
