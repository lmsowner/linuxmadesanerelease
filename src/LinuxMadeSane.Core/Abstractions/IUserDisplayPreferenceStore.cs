// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface IUserDisplayPreferenceStore
{
    Task<UserDisplayPreference?> GetAsync(Guid userId, CancellationToken cancellationToken = default);
    Task SaveAsync(UserDisplayPreference preference, CancellationToken cancellationToken = default);
}
