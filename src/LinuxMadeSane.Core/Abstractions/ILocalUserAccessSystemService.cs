// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalUserAccessSystemService
{
    Task ApplyPoliciesAsync(IReadOnlyList<LocalUserAccessPolicy> policies, CancellationToken cancellationToken = default);

    Task ResetPasswordAsync(string userName, string newPassword, CancellationToken cancellationToken = default);
}
