// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Core.Abstractions;

public interface ISavedCredentialAccessContext
{
    // An enabled account ID, Guid.Empty for explicitly trusted host access, or null when denied.
    Task<Guid?> GetAuthenticatedUserIdAsync(CancellationToken token = default);
}
