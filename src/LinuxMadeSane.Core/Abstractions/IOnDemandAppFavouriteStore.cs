// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Abstractions;

public interface IOnDemandAppFavouriteStore
{
    Task<IReadOnlySet<string>> GetAsync(string userId, CancellationToken cancellationToken = default);
    Task SetAsync(string userId, string serviceKey, bool isFavourite, CancellationToken cancellationToken = default);
}
