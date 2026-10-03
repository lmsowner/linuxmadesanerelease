// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SavedCredentialConnectionService(ISavedConnectionCredentialService store,
    ISavedCredentialAccessContext access, ISavedCredentialConnectionTransport transport,
    ISshKeyPairGenerator generator) : ISavedCredentialConnectionService
{
    public async Task TestAsync(Guid userId, SavedConnectionCredentialEditor editor, CancellationToken token = default)
        => await transport.TestAsync(await ResolveAsync(userId, editor, token), token);

    public async Task<SavedConnectionCredentialSummary> SetUpKeyAsync(Guid userId, SavedConnectionCredentialEditor editor, CancellationToken token = default)
    {
        var password = await ResolveAsync(userId, editor, token);
        if (password.Kind != ConnectionCredentialKind.Ssh)
            throw new InvalidOperationException("Key setup requires SSH password credentials.");
        await transport.TestAsync(password, token);
        var key = await generator.GenerateAsync(comment: password.Name, cancellationToken: token);
        await transport.InstallPublicKeyAsync(password, key.PublicKey, token);
        var replacement = new SavedConnectionCredentialEditor
        {
            Name = password.Name.Length > 74 ? password.Name[..74] + " (key)" : password.Name + " (key)",
            Kind = ConnectionCredentialKind.SshKeyPair, Server = password.Server, Port = password.Port,
            Username = password.Username, PrivateKey = key.PrivateKey, PublicKey = key.PublicKey
        };
        // This is a separate connection with no password fallback. Never replace a
        // working credential before the remote server accepts the new key.
        await transport.TestAsync(replacement, token);
        return await store.SaveAsync(userId, replacement, token);
    }

    public async Task<SavedConnectionCredentialSummary> UseExistingKeyAsync(Guid userId, SavedConnectionCredentialEditor editor, Guid keyId, CancellationToken token = default)
    {
        var (_, key, connection) = await ResolveKeyAsync(userId, editor, keyId, token);
        await transport.TestAsync(connection, token);
        return await SaveKeyScopeAsync(userId, key, connection, token);
    }

    public async Task<SavedConnectionCredentialSummary> InstallExistingKeyAsync(Guid userId, SavedConnectionCredentialEditor editor, Guid keyId, CancellationToken token = default)
    {
        var (password, key, connection) = await ResolveKeyAsync(userId, editor, keyId, token);
        await transport.TestAsync(password, token);
        // Prefer a key already accepted by the account. Do not append it again.
        try { await transport.TestAsync(connection, token); }
        catch (Renci.SshNet.Common.SshAuthenticationException)
        {
            await transport.InstallPublicKeyAsync(password, key.Summary.PublicKey, token);
            await transport.TestAsync(connection, token);
        }
        return await SaveKeyScopeAsync(userId, key, connection, token);
    }

    private async Task<(SavedConnectionCredentialEditor, SavedConnectionCredential, SavedConnectionCredentialEditor)> ResolveKeyAsync(
        Guid userId, SavedConnectionCredentialEditor editor, Guid keyId, CancellationToken token)
    {
        var password = await ResolveAsync(userId, editor, token);
        var key = await store.ResolveAsync(userId, keyId, token) ?? throw new InvalidOperationException("Saved key not found.");
        if (!SavedCredentialKeySuggestions.Matches(key.Summary, password) || string.IsNullOrWhiteSpace(key.PrivateKey) || string.IsNullOrWhiteSpace(key.Summary.PublicKey))
            throw new InvalidOperationException("Choose a saved key for this server and account, or an unrestricted reusable key.");
        return (password, key, new SavedConnectionCredentialEditor
        {
            Name = password.Name.Length > 74 ? password.Name[..74] + " (key)" : password.Name + " (key)",
            Kind = ConnectionCredentialKind.SshKeyPair, Server = password.Server, Port = password.Port,
            Username = password.Username, PrivateKey = key.PrivateKey, PublicKey = key.Summary.PublicKey, Passphrase = key.Passphrase
        });
    }

    private async Task<SavedConnectionCredentialSummary> SaveKeyScopeAsync(Guid userId, SavedConnectionCredential key,
        SavedConnectionCredentialEditor connection, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(key.Summary.Server) && !string.IsNullOrWhiteSpace(key.Summary.Username)) return key.Summary;
        // Keep the reusable credential intact and store the tested server/account
        // binding so selecting it in a connection form also fills those details.
        return await store.SaveAsync(userId, connection, token);
    }

    private async Task<SavedConnectionCredentialEditor> ResolveAsync(Guid userId, SavedConnectionCredentialEditor editor, CancellationToken token)
    {
        if (await access.GetAuthenticatedUserIdAsync(token) != userId)
            throw new UnauthorizedAccessException("This session cannot test credentials.");
        var saved = editor.Id.HasValue ? await store.ResolveAsync(userId, editor.Id.Value, token)
            ?? throw new InvalidOperationException("Credential not found.") : null;
        return new SavedConnectionCredentialEditor
        {
            Name = editor.Name, Kind = editor.Kind, Server = editor.Server.Trim(), Port = editor.Port,
            Username = editor.Username.Trim(), Domain = editor.Domain,
            Password = editor.ClearPassword ? "" : editor.Password.Length > 0 ? editor.Password : saved?.Password ?? "",
            PrivateKey = editor.PrivateKey.Length > 0 ? editor.PrivateKey : saved?.PrivateKey ?? "",
            Passphrase = editor.ClearPassphrase ? "" : editor.Passphrase.Length > 0 ? editor.Passphrase : saved?.Passphrase ?? ""
        };
    }
}
