// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
namespace LinuxMadeSane.Core.Abstractions;

public interface ISavedCredentialAccessContext
{
    // An enabled account ID, Guid.Empty for explicitly trusted host access, or null when denied.
    Task<Guid?> GetAuthenticatedUserIdAsync(CancellationToken token = default);
}
