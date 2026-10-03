// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SavedCredentialConnectionService(ISavedConnectionCredentialService store,
    ISavedCredentialAccessContext access, ISavedCredentialConnectionTransport transport,
    ISshKeyPairGenerator generator, IManagedHostStore? hosts = null,
    IHostSecretsService? hostSecrets = null) : ISavedCredentialConnectionService
{
    public async Task TestAsync(Guid userId, SavedConnectionCredentialEditor editor, CancellationToken token = default)
        => await transport.TestAsync(await ResolveAsync(userId, editor, token), token);

    public async Task<SavedConnectionCredentialSummary> SetUpKeyAsync(Guid userId, SavedConnectionCredentialEditor editor, CancellationToken token = default)
    {
        var password = await ResolveAsync(userId, editor, token);
        if (password.Kind != ConnectionCredentialKind.Ssh)
            throw new InvalidOperationException("Key setup requires SSH password credentials.");
        try { await transport.TestAsync(password, token); }
        catch (Exception ex) { throw Failure("Password login failed", ex, false); }
        LinuxMadeSane.Core.Models.GeneratedSshKeyPair key;
        try { key = await generator.GenerateAsync(comment: password.Name, cancellationToken: token); }
        catch (Exception ex) { throw Failure("Key generation failed", ex, false); }
        var replacement = new SavedConnectionCredentialEditor
        {
            Name = password.Name.Length > 74 ? password.Name[..74] + " (key)" : password.Name + " (key)",
            Kind = ConnectionCredentialKind.SshKeyPair, Server = password.Server, Port = password.Port,
            Username = password.Username, PrivateKey = key.PrivateKey, PublicKey = key.PublicKey
        };
        // Keep the encrypted private key before any remote mutation. A failed
        // install/verification can then retry the same key instead of losing it.
        SavedConnectionCredentialSummary saved;
        try { saved = await store.SaveAsync(userId, replacement, token); }
        catch (Exception ex) { throw Failure("Could not securely save the new key; nothing was installed", ex, false); }
        try { await transport.InstallPublicKeyAsync(password, key.PublicKey, token); }
        catch (Exception ex) { throw Failure("Public key installation failed", ex, true); }
        // This connection contains no password fallback.
        try { await transport.TestAsync(replacement, token); }
        catch (Exception ex) { throw Failure("Key-only login verification failed", ex, true); }
        return saved;
    }

    private static SavedCredentialKeySetupException Failure(string stage, Exception ex, bool retained)
    {
        var reason = ex switch
        {
            OperationCanceledException => "The operation timed out or was cancelled.",
            Renci.SshNet.Common.SshAuthenticationException => "The SSH server rejected this authentication method. Check the account's SSH login policy and authorized keys.",
            System.Net.Sockets.SocketException => "The server could not be reached. Check its name, port and network access.",
            Renci.SshNet.Common.SshOperationTimeoutException => "The SSH server did not respond in time.",
            InvalidOperationException => ex.Message,
            _ => $"The SSH operation failed ({ex.GetType().Name})."
        };
        return new($"{stage}. {reason} " + (retained ? "The new key is kept in the credential store; use the saved key to test or retry installation. " : "") + "Your password credential is unchanged.", ex);
    }

    public async Task<SavedConnectionCredentialSummary> UseManagedHostKeyAsync(Guid userId,
        SavedConnectionCredentialEditor editor, Guid hostId, bool installIfNeeded, CancellationToken token = default)
    {
        var password = await ResolveAsync(userId, editor, token);
        if (password.Kind != ConnectionCredentialKind.Ssh || hosts is null || hostSecrets is null)
            throw new InvalidOperationException("Existing host keys require SSH credentials.");
        var host = await hosts.GetAsync(hostId, token) ?? throw new InvalidOperationException("Managed host not found.");
        if (string.IsNullOrWhiteSpace(host.PrivateKeySecretReference)) throw new InvalidOperationException("This host has no saved private key.");
        var privateKey = await hostSecrets.ResolveSecretAsync(host.PrivateKeySecretReference, token);
        var passphrase = string.IsNullOrWhiteSpace(host.PrivateKeyPassphraseSecretReference) ? "" :
            await hostSecrets.ResolveSecretAsync(host.PrivateKeyPassphraseSecretReference, token) ?? "";
        if (string.IsNullOrWhiteSpace(privateKey)) throw new InvalidOperationException("This host's saved key could not be read.");
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(privateKey));
        using var parsed = new Renci.SshNet.PrivateKeyFile(stream, passphrase);
        var data = parsed.HostKeyAlgorithms.First().Data;
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4));
        var publicKey = System.Text.Encoding.UTF8.GetString(data, 4, length) + " " + Convert.ToBase64String(data);
        var connection = new SavedConnectionCredentialEditor { Name = password.Name.Length > 74 ? password.Name[..74] + " (key)" : password.Name + " (key)",
            Kind = ConnectionCredentialKind.SshKeyPair, Server = password.Server, Port = password.Port,
            Username = password.Username, PrivateKey = privateKey, PublicKey = publicKey, Passphrase = passphrase };
        try { await transport.TestAsync(connection, token); }
        catch (Renci.SshNet.Common.SshAuthenticationException) when (installIfNeeded)
        {
            try { await transport.TestAsync(password, token); }
            catch (Exception ex) { throw Failure("Password login failed", ex, false); }
            var retained = await store.SaveAsync(userId, connection, token);
            try { await transport.InstallPublicKeyAsync(password, publicKey, token); }
            catch (Exception ex) { throw Failure("Public key installation failed", ex, true); }
            try { await transport.TestAsync(connection, token); }
            catch (Exception ex) { throw Failure("Key-only login verification failed", ex, true); }
            return retained;
        }
        return await store.SaveAsync(userId, connection, token);
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
        // Prefer a key already accepted by the account. Do not append it again.
        try { await transport.TestAsync(connection, token); }
        catch (Renci.SshNet.Common.SshAuthenticationException)
        {
            try { await transport.TestAsync(password, token); }
            catch (Exception ex) { throw Failure("Password login failed", ex, false); }
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
