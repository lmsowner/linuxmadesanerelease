namespace LinuxMadeSane.Core.Models;

public sealed record LocalInstanceIdentity(
    Guid InstanceId,
    string DisplayName,
    string PrivateKeySecretReference,
    string PublicKey,
    string PublicKeyFingerprint,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? RegisteredWithPublicSiteAtUtc);
