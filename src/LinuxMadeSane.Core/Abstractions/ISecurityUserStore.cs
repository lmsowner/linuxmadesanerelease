// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISecurityUserStore
{
    Task<IReadOnlyList<SecurityUser>> ListAsync(CancellationToken cancellationToken = default);
    Task<SecurityUser?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<SecurityUser?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);
    Task<SecurityUser?> FindByLinuxUsernameAsync(string linuxUsername, CancellationToken cancellationToken = default);
    Task SaveAsync(SecurityUser user, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
