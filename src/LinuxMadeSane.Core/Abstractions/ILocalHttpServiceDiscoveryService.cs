// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalHttpServiceDiscoveryService
{
    Task<IReadOnlyList<LocalHttpServiceEndpoint>> GetCachedAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalHttpServiceEndpoint>> DiscoverAsync(
        LocalHttpServiceDiscoveryRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalHttpServiceEndpoint>> DiscoverAsync(
        LocalHttpServiceDiscoveryRequest request,
        IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalHttpServiceEndpoint>> DiscoverAsync(CancellationToken cancellationToken = default);
}
