// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Security.Cryptography;
using System.Text.Json;
namespace LinuxMadeSane.Core.Licensing;

// Exact UTF-8 payload bytes are signed, encoded as base64; no JSON reserialisation ambiguity.
// ECDSA P-256 / SHA-256, IEEE P1363 fixed-length signatures, supported by the .NET cryptography library.
public sealed class EntitlementVerifier(IReadOnlyDictionary<string, string> trustedPublicKeys)
{
    private static bool TrustedIssuer(string? issuer) => issuer == "LMS Developer" || issuer is not null &&
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(issuer))) == "afe54fec8e3779cf269b84b9732ec95809e8d0796950d277c20aa1e1e4c9c08b";
    public Entitlement Verify(SignedEntitlement envelope, Guid organisation)
    {
        if (envelope is null || envelope.Payload is null || envelope.Signature is null || envelope.KeyId is null || envelope.Algorithm != "ES256-P1363" || envelope.Payload.Length > 32768 || envelope.Signature.Length > 128 ||
            !trustedPublicKeys.TryGetValue(envelope.KeyId, out var publicKey)) throw new ArgumentException("Untrusted or malformed licence.");
        try
        {
            var bytes = Convert.FromBase64String(envelope.Payload);
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKey);
            if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" || !key.VerifyData(bytes, Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new ArgumentException("Invalid licence signature.");
            var payload = JsonSerializer.Deserialize<Entitlement>(bytes) ?? throw new ArgumentException("Missing payload.");
            if (payload.SchemaVersion != 1 || payload.EntitlementId == Guid.Empty || payload.OrganisationId != organisation ||
                payload.Product != "lms-professional" || !TrustedIssuer(payload.Issuer) || payload.KeyId != envelope.KeyId ||
                payload.RightsVersion != "lms-professional-v1" || payload.ServerCapacity < 1 || payload.ServerCapacity > 10000000 ||
                payload.IssuedAtUtc > payload.NotBeforeUtc || payload.NotBeforeUtc >= payload.ExpiresAtUtc ||
                payload.Rights is null || !payload.Rights.Contains("operational") || payload.Rights.Any(r => r is not ("operational" or "redistribution")) ||
                payload.Rights.Contains("redistribution") && string.IsNullOrWhiteSpace(payload.AgreementReference))
                throw new ArgumentException("Licence fields are not valid for this organisation.");
            return payload;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        { throw new ArgumentException("Invalid licence format or signature."); }
    }
}
