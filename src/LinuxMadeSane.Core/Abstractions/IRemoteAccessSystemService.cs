// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface IRemoteAccessSystemService
{
    Task<bool> EnsureLocalAccountAsync(string linuxUsername, CancellationToken cancellationToken = default);
    Task ApplySshAccessConfigurationAsync(IReadOnlyList<SecurityUser> users, CancellationToken cancellationToken = default);
    Task ResetLocalPasswordAsync(string linuxUsername, string newPassword, CancellationToken cancellationToken = default);
    Task DeleteLocalAccountAsync(string linuxUsername, CancellationToken cancellationToken = default);
}
