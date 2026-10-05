// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Services;

public sealed class SshfsCredentialRecoveryService(
    IManagedHostService hosts,
    ISavedConnectionCredentialService credentials,
    ISavedCredentialConnectionService connections)
{
    public async Task ApplyVerifiedKeyAsync(Guid userId, Guid hostId, Guid credentialId, CancellationToken token = default)
    {
        var credential = await credentials.ResolveAsync(userId, credentialId, token)
            ?? throw new InvalidOperationException("The selected saved credential is no longer available.");
        if (!credential.Summary.HasPrivateKey || string.IsNullOrWhiteSpace(credential.PrivateKey))
            throw new InvalidOperationException("Set up or select a saved SSH key before retrying the mount.");
        if (credential.Summary.HasPassphrase || !string.IsNullOrWhiteSpace(credential.Passphrase))
            throw new InvalidOperationException("Choose a non-interactive mount key without a passphrase.");

        var host = await hosts.GetEditorAsync(hostId, token)
            ?? throw new InvalidOperationException("The selected SSH host no longer exists.");
        if (!string.IsNullOrWhiteSpace(credential.Summary.Username) && credential.Summary.Username != host.Username)
            throw new InvalidOperationException($"Choose a key for {host.Username}, or a reusable keypair without a username.");

        // Test against the mount's original server and user, using key authentication
        // only. A successful password fallback must never count as a repaired key.
        await connections.TestAsync(userId, new SavedConnectionCredentialEditor
        {
            Name = credential.Summary.Name, Kind = ConnectionCredentialKind.SshKeyPair,
            Server = host.Hostname, Port = host.Port, Username = host.Username,
            PrivateKey = credential.PrivateKey
        }, token);

        host.PrivateKey = credential.PrivateKey;
        host.PrivateKeyPassphrase = "";
        host.ClearStoredPrivateKeyPassphrase = true;
        host.ClearStoredPrivateKey = false;
        host.PrimaryAuthenticationType = AuthenticationType.PrivateKey;
        if (host.FallbackAuthenticationType == AuthenticationType.PrivateKey)
            host.FallbackAuthenticationType = null;
        // Keep existing password recovery material; no changes before the key test passes.
        await hosts.SaveHostAsync(host, token);
    }
}
