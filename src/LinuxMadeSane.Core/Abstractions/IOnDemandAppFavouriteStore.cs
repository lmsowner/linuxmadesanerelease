// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Core.Abstractions;

public interface IOnDemandAppFavouriteStore
{
    Task<IReadOnlyList<OnDemandAppFavourite>> ListAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OnDemandAppFavourite>> ListAllAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        string userId,
        OnDemandAppFavourite favourite,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        string userId,
        string serviceKey,
        CancellationToken cancellationToken = default);

    Task RefreshEndpointsAsync(
        IReadOnlyList<LocalHttpServiceEndpoint> endpoints,
        CancellationToken cancellationToken = default);
}
