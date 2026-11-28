// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface IUserDisplayPreferenceStore
{
    Task<UserDisplayPreference?> GetAsync(Guid userId, CancellationToken cancellationToken = default);
    Task SaveAsync(UserDisplayPreference preference, CancellationToken cancellationToken = default);
}
