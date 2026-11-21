// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using System.Security.Cryptography;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Abstractions.Portal;
using LinuxMadeSane.Core.Models.Portal;

namespace LinuxMadeSane.Application.Services;

public sealed class InstanceIdentityService(
    IPortalConnectionStore settingsStore,
    ISecretStore secretStore) : IInstanceIdentityService
{
    public async Task<PortalInstanceIdentity> GetOrCreateIdentityAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.GetAsync(cancellationToken)
            ?? throw new InvalidOperationException("Portal connection settings have not been initialized.");

        if (!string.IsNullOrWhiteSpace(settings.InstanceIdentityPrivateKeySecretReference) &&
            !string.IsNullOrWhiteSpace(settings.InstanceIdentityPublicKey) &&
            !string.IsNullOrWhiteSpace(settings.InstanceIdentityPublicKeyFingerprint))
        {
            var storedPrivateKey = await secretStore.ResolveSecretAsync(
                settings.InstanceIdentityPrivateKeySecretReference,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(storedPrivateKey))
            {
                return new PortalInstanceIdentity(
                    settings.LocalInstanceId,
                    settings.InstanceIdentityPrivateKeySecretReference,
                    settings.InstanceIdentityPublicKey,
                    settings.InstanceIdentityPublicKeyFingerprint);
            }
        }

        using var identityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = Convert.ToBase64String(identityKey.ExportPkcs8PrivateKey());
        var publicKeyBytes = identityKey.ExportSubjectPublicKeyInfo();
        var publicKey = Convert.ToBase64String(publicKeyBytes);
        var publicKeyFingerprint = Convert.ToHexString(SHA256.HashData(publicKeyBytes));
        var secretReference = await secretStore.StoreSecretAsync(
            privateKey,
            $"portal-instance-identity:{settings.LocalInstanceId:N}",
            cancellationToken);

        var next = settings with
        {
            InstanceIdentityPrivateKeySecretReference = secretReference,
            InstanceIdentityPublicKey = publicKey,
            InstanceIdentityPublicKeyFingerprint = publicKeyFingerprint,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await settingsStore.SaveAsync(next, cancellationToken);

        return new PortalInstanceIdentity(
            next.LocalInstanceId,
            secretReference,
            publicKey,
            publicKeyFingerprint);
    }
}
