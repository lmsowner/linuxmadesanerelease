// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Security.Cryptography;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Services;

public sealed class LocalInstanceIdentityService(
    ILocalInstanceIdentityStore identityStore,
    ISecretStore secretStore)
{
    public async Task<LocalInstanceIdentity> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        var existing = await identityStore.GetAsync(cancellationToken);
        if (existing is not null &&
            !string.IsNullOrWhiteSpace(existing.PrivateKeySecretReference) &&
            !string.IsNullOrWhiteSpace(existing.PublicKey) &&
            !string.IsNullOrWhiteSpace(existing.PublicKeyFingerprint) &&
            !string.IsNullOrWhiteSpace(await secretStore.ResolveSecretAsync(existing.PrivateKeySecretReference, cancellationToken)))
        {
            return existing;
        }

        using var identityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = Convert.ToBase64String(identityKey.ExportPkcs8PrivateKey());
        var publicKeyBytes = identityKey.ExportSubjectPublicKeyInfo();
        var now = DateTimeOffset.UtcNow;
        var instanceId = existing?.InstanceId ?? Guid.NewGuid();
        var secretReference = await secretStore.StoreSecretAsync(
            privateKey,
            $"public-site-passkey-relay:{instanceId:N}",
            cancellationToken);

        var identity = new LocalInstanceIdentity(
            instanceId,
            existing?.DisplayName ?? Environment.MachineName,
            secretReference,
            Convert.ToBase64String(publicKeyBytes),
            Convert.ToHexString(SHA256.HashData(publicKeyBytes)),
            existing?.CreatedAtUtc ?? now,
            now,
            existing?.RegisteredWithPublicSiteAtUtc);

        await identityStore.SaveAsync(identity, cancellationToken);
        return identity;
    }

    public async Task<byte[]> SignAsync(LocalInstanceIdentity identity, string canonicalPayload, CancellationToken cancellationToken)
    {
        var privateKey = await secretStore.ResolveSecretAsync(identity.PrivateKeySecretReference, cancellationToken);
        if (string.IsNullOrWhiteSpace(privateKey))
        {
            throw new InvalidOperationException("The local LMS instance identity key is unavailable.");
        }

        using var signingKey = ECDsa.Create();
        signingKey.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);
        return signingKey.SignData(
            System.Text.Encoding.UTF8.GetBytes(canonicalPayload),
            HashAlgorithmName.SHA256);
    }

    public async Task MarkRegisteredAsync(LocalInstanceIdentity identity, CancellationToken cancellationToken)
    {
        await identityStore.SaveAsync(identity with
        {
            RegisteredWithPublicSiteAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }
}
