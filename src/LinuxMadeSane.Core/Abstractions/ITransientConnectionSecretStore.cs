// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

// Guardrail: raw connection secrets stay in this transient store only. Shared page state
// and detached-window snapshots must pass handles instead of copying secret text around.
public interface ITransientConnectionSecretStore
{
    ManagedHostConnectionSecrets Get(Guid? secretHandle);

    Guid? Save(
        Guid? existingSecretHandle,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase);

    Guid? Clone(Guid? secretHandle);

    void Delete(Guid? secretHandle);
}
