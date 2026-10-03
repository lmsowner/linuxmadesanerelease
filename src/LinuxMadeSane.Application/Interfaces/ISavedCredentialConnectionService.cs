// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using LinuxMadeSane.Application.Contracts;
namespace LinuxMadeSane.Application.Interfaces;

public interface ISavedCredentialConnectionService
{
    Task TestAsync(Guid userId, SavedConnectionCredentialEditor editor, CancellationToken token = default);
    Task<SavedConnectionCredentialSummary> SetUpKeyAsync(Guid userId, SavedConnectionCredentialEditor editor, CancellationToken token = default);
}

public interface ISavedCredentialConnectionTransport
{
    Task TestAsync(SavedConnectionCredentialEditor editor, CancellationToken token);
    Task InstallPublicKeyAsync(SavedConnectionCredentialEditor passwordCredential, string publicKey, CancellationToken token);
}
