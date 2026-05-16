// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Collections.Concurrent;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Services;

// Guardrail: transient passwords and private keys live here only. Workspace state,
// detached-window snapshots, and clipboard models must carry a handle instead of raw
// secret text so shared UI flows do not become an ad hoc secret transport layer.
public sealed class TransientConnectionSecretStore : ITransientConnectionSecretStore
{
    private readonly ConcurrentDictionary<Guid, ManagedHostConnectionSecrets> secrets = new();

    public ManagedHostConnectionSecrets Get(Guid? secretHandle)
    {
        if (!secretHandle.HasValue)
        {
            return ManagedHostConnectionSecrets.Empty;
        }

        return secrets.TryGetValue(secretHandle.Value, out var snapshot)
            ? snapshot
            : ManagedHostConnectionSecrets.Empty;
    }

    public Guid? Save(
        Guid? existingSecretHandle,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase)
    {
        var snapshot = new ManagedHostConnectionSecrets(
            password ?? string.Empty,
            privateKey ?? string.Empty,
            privateKeyPassphrase ?? string.Empty);
        if (!snapshot.HasAnyValue)
        {
            Delete(existingSecretHandle);
            return null;
        }

        var secretHandle = existingSecretHandle ?? Guid.NewGuid();
        secrets[secretHandle] = snapshot;
        return secretHandle;
    }

    public Guid? Clone(Guid? secretHandle)
    {
        var snapshot = Get(secretHandle);
        return snapshot.HasAnyValue
            ? Save(null, snapshot.Password, snapshot.PrivateKey, snapshot.PrivateKeyPassphrase)
            : null;
    }

    public void Delete(Guid? secretHandle)
    {
        if (secretHandle.HasValue)
        {
            secrets.TryRemove(secretHandle.Value, out _);
        }
    }
}
