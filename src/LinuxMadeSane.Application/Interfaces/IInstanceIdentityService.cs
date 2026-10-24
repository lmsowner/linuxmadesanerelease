// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Portal;

namespace LinuxMadeSane.Application.Interfaces;

public interface IInstanceIdentityService
{
    Task<PortalInstanceIdentity> GetOrCreateIdentityAsync(CancellationToken cancellationToken = default);
}
