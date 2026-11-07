// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalUserAccessSystemService
{
    Task ApplyPoliciesAsync(IReadOnlyList<LocalUserAccessPolicy> policies, CancellationToken cancellationToken = default);

    Task ResetPasswordAsync(string userName, string newPassword, CancellationToken cancellationToken = default);
}
