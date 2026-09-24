// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions.Portal;
using LinuxMadeSane.Core.Models.Portal;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class DisabledPortalConnectionStore : IPortalConnectionStore
{
    public Task<PortalConnectionSettings?> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<PortalConnectionSettings?>(null);

    public Task SaveAsync(PortalConnectionSettings settings, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
