// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Portal;

namespace LinuxMadeSane.Core.Abstractions.Portal;

public interface IPortalConnectionStore
{
    Task<PortalConnectionSettings?> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(PortalConnectionSettings settings, CancellationToken cancellationToken = default);
}
