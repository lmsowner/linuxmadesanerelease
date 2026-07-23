// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Services;

public sealed class UserManagedHostCredentialProfileService(
    IUserManagedHostCredentialProfileStore store,
    ISecretStore secretStore) : IUserManagedHostCredentialProfileService
{
    private const int MaxNameLength = 80;
    private const int MaxUsernameLength = 128;

    public async Task<IReadOnlyList<UserManagedHostCredentialProfileSummary>> ListAsync(
        Guid userId,
        Guid managedHostId,
        CancellationToken cancellationToken = default)
    {
        var profiles = await store.ListAsync(userId, managedHostId, cancellationToken);
        return profiles.Select(MapSummary).ToArray();
    }

    public async Task<UserManagedHostCredentialProfileCredentials?> ResolveAsync(
        Guid userId,
        Guid managedHostId,
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var profile = await store.GetAsync(profileId, cancellationToken);
        if (profile is null || profile.UserId != userId || profile.ManagedHostId != managedHostId)
        {
            return null;
        }

        return new UserManagedHostCredentialProfileCredentials(
            profile.Id,
            profile.Name,
            profile.Username,
            await ResolveOptionalSecretAsync(profile.PasswordSecretReference, cancellationToken),
            await ResolveOptionalSecretAsync(profile.PrivateKeySecretReference, cancellationToken),
            await ResolveOptionalSecretAsync(profile.PrivateKeyPassphraseSecretReference, cancellationToken));
    }

    public async Task<UserManagedHostCredentialProfileSummary> SaveAsync(
        UserManagedHostCredentialProfileEditor editor,
        CancellationToken cancellationToken = default)
    {
        var name = NormalizeName(editor.Name, editor.Username);
        var username = NormalizeUsername(editor.Username);
        var existing = await ResolveExistingAsync(editor, name, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var profileId = existing?.Id ?? Guid.NewGuid();
        var passwordReference = await UpdateSecretAsync(
            existing?.PasswordSecretReference,
            editor.Password,
            editor.ClearStoredPassword,
            $"SSH profile password {profileId:N}",
            cancellationToken);
        var privateKeyReference = await UpdateSecretAsync(
            existing?.PrivateKeySecretReference,
            editor.PrivateKey,
            editor.ClearStoredPrivateKey,
            $"SSH profile private key {profileId:N}",
            cancellationToken);
        var privateKeyPassphraseReference = await UpdateSecretAsync(
            existing?.PrivateKeyPassphraseSecretReference,
            editor.PrivateKeyPassphrase,
            editor.ClearStoredPrivateKeyPassphrase,
            $"SSH profile private key passphrase {profileId:N}",
            cancellationToken);

        if (string.IsNullOrWhiteSpace(passwordReference) && string.IsNullOrWhiteSpace(privateKeyReference))
        {
            throw new InvalidOperationException("Save a password or private key for this connection profile.");
        }

        var profile = new UserManagedHostCredentialProfile(
            profileId,
            editor.UserId,
            editor.ManagedHostId,
            name,
            username,
            passwordReference,
            privateKeyReference,
            privateKeyPassphraseReference,
            existing?.CreatedAtUtc ?? now,
            now);

        await store.SaveAsync(profile, cancellationToken);
        return MapSummary(profile);
    }

    public async Task DeleteAsync(
        Guid userId,
        Guid managedHostId,
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var profile = await store.GetAsync(profileId, cancellationToken);
        if (profile is null || profile.UserId != userId || profile.ManagedHostId != managedHostId)
        {
            return;
        }

        await store.DeleteAsync(profile.Id, cancellationToken);
        await DeleteSecretIfPresentAsync(profile.PasswordSecretReference, cancellationToken);
        await DeleteSecretIfPresentAsync(profile.PrivateKeySecretReference, cancellationToken);
        await DeleteSecretIfPresentAsync(profile.PrivateKeyPassphraseSecretReference, cancellationToken);
    }

    private async Task<UserManagedHostCredentialProfile?> ResolveExistingAsync(
        UserManagedHostCredentialProfileEditor editor,
        string normalizedName,
        CancellationToken cancellationToken)
    {
        if (editor.Id.HasValue)
        {
            var existing = await store.GetAsync(editor.Id.Value, cancellationToken);
            if (existing is null ||
                existing.UserId != editor.UserId ||
                existing.ManagedHostId != editor.ManagedHostId)
            {
                throw new InvalidOperationException("Credential profile not found.");
            }

            return existing;
        }

        return await store.FindByNameAsync(
            editor.UserId,
            editor.ManagedHostId,
            normalizedName,
            cancellationToken);
    }

    private async Task<string?> UpdateSecretAsync(
        string? existingReference,
        string value,
        bool clearExisting,
        string purpose,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            var newReference = await secretStore.StoreSecretAsync(value, purpose, cancellationToken);
            await DeleteSecretIfPresentAsync(existingReference, cancellationToken);
            return newReference;
        }

        if (clearExisting)
        {
            await DeleteSecretIfPresentAsync(existingReference, cancellationToken);
            return null;
        }

        return existingReference;
    }

    private async Task<string> ResolveOptionalSecretAsync(
        string? secretReference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secretReference))
        {
            return string.Empty;
        }

        return await secretStore.ResolveSecretAsync(secretReference, cancellationToken) ?? string.Empty;
    }

    private async Task DeleteSecretIfPresentAsync(
        string? secretReference,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(secretReference))
        {
            await secretStore.DeleteSecretAsync(secretReference, cancellationToken);
        }
    }

    private static UserManagedHostCredentialProfileSummary MapSummary(UserManagedHostCredentialProfile profile) =>
        new(
            profile.Id,
            profile.Name,
            profile.Username,
            !string.IsNullOrWhiteSpace(profile.PasswordSecretReference),
            !string.IsNullOrWhiteSpace(profile.PrivateKeySecretReference),
            !string.IsNullOrWhiteSpace(profile.PrivateKeyPassphraseSecretReference),
            profile.UpdatedAtUtc);

    private static string NormalizeName(string name, string username)
    {
        var normalized = string.IsNullOrWhiteSpace(name)
            ? username.Trim()
            : name.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("A profile name is required.");
        }

        return normalized.Length > MaxNameLength ? normalized[..MaxNameLength] : normalized;
    }

    private static string NormalizeUsername(string username)
    {
        var normalized = username.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("A Linux username is required.");
        }

        return normalized.Length > MaxUsernameLength ? normalized[..MaxUsernameLength] : normalized;
    }
}
