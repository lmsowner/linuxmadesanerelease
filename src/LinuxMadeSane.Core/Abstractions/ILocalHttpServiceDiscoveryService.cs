// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalHttpServiceDiscoveryService
{
    Task<IReadOnlyList<LocalHttpServiceEndpoint>> GetCachedAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalHttpServiceEndpoint>> ValidateCachedAsync(CancellationToken cancellationToken = default) => GetCachedAsync(cancellationToken);

    Task<int> FlushCacheAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalHttpServiceEndpoint>> DiscoverAsync(
        LocalHttpServiceDiscoveryRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalHttpServiceEndpoint>> DiscoverAsync(
        LocalHttpServiceDiscoveryRequest request,
        IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalHttpServiceEndpoint>> DiscoverAsync(CancellationToken cancellationToken = default);
}
