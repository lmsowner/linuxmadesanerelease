// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Portal;

namespace LinuxMadeSane.Core.Abstractions.Portal;

public interface IPortalConnectionStore
{
    Task<PortalConnectionSettings?> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(PortalConnectionSettings settings, CancellationToken cancellationToken = default);
}
