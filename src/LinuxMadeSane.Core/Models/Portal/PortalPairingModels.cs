// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Text.Json.Serialization;

namespace LinuxMadeSane.Core.Models.Portal;

public sealed record PortalInstanceIdentity(
    Guid LocalInstanceId,
    string PrivateKeySecretReference,
    string PublicKey,
    string PublicKeyFingerprint);

public sealed record PortalPairingRequestPayload(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("iid")] Guid LocalInstanceId,
    [property: JsonPropertyName("dn")] string InstanceDisplayName,
    [property: JsonPropertyName("mn")] string MachineName,
    [property: JsonPropertyName("av")] string AppVersion,
    [property: JsonPropertyName("pc")] string PairingCode,
    [property: JsonPropertyName("n")] string Nonce,
    [property: JsonPropertyName("exp")] DateTimeOffset ExpiresAtUtc,
    [property: JsonPropertyName("u")] string PortalBaseUrl,
    [property: JsonPropertyName("pk")] string InstancePublicKey,
    [property: JsonPropertyName("fp")] string InstancePublicKeyFingerprint);

public sealed record PortalSignedPairingRequest(
    [property: JsonPropertyName("p")] PortalPairingRequestPayload Payload,
    [property: JsonPropertyName("s")] string Signature);

public sealed record PortalPairingArtifact(
    PortalPairingRequestPayload Payload,
    string SignedToken,
    string PairingUrl);

public sealed record PortalPairingValidationResult(
    bool Succeeded,
    string Message,
    PortalPairingRequestPayload? Payload);
