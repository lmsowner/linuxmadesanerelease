namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpPublicKey(
    Guid Id,
    string Label,
    string KeyType,
    string Fingerprint,
    string PublicKeyText,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
