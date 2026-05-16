// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Portal;

public sealed record PortalConnectionSettings(
    int Id,
    Guid LocalInstanceId,
    string PortalBaseUrl,
    string InstanceDisplayName,
    bool IsEnabled,
    string? InstanceIdentityPrivateKeySecretReference,
    string InstanceIdentityPublicKey,
    string InstanceIdentityPublicKeyFingerprint,
    string PairingCode,
    DateTimeOffset? PairingCodeGeneratedAtUtc,
    DateTimeOffset? PairingCodeExpiresAtUtc,
    Guid? PortalOrganizationId,
    string? PortalOrganizationName,
    Guid? PortalInstanceId,
    string? PortalApiKeyId,
    string? PortalApiSecretReference,
    string LastConnectionStatus,
    DateTimeOffset? LastConnectedAtUtc,
    DateTimeOffset? LastHeartbeatAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
