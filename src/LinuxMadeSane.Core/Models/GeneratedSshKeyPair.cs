namespace LinuxMadeSane.Core.Models;

public sealed record GeneratedSshKeyPair(
    string Algorithm,
    string PrivateKey,
    string PublicKey,
    string FingerprintSha256);
