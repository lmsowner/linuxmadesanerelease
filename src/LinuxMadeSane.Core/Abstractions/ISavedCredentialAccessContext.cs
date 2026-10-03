// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
namespace LinuxMadeSane.Core.Abstractions;

public interface ISavedCredentialAccessContext
{
    Task<Guid?> GetAuthenticatedUserIdAsync(CancellationToken token = default);
}
