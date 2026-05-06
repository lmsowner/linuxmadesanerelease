namespace LinuxMadeSane.Core.Models;

public static class SshKeyMaterialClassifier
{
    public static bool LooksLikePublicKey(string? keyMaterial)
    {
        if (string.IsNullOrWhiteSpace(keyMaterial))
        {
            return false;
        }

        var normalizedKey = keyMaterial.Trim();
        return normalizedKey.StartsWith("ssh-", StringComparison.Ordinal) ||
               normalizedKey.StartsWith("ecdsa-", StringComparison.Ordinal) ||
               normalizedKey.StartsWith("sk-ssh-", StringComparison.Ordinal) ||
               normalizedKey.StartsWith("sk-ecdsa-", StringComparison.Ordinal) ||
               normalizedKey.StartsWith("---- BEGIN SSH2 PUBLIC KEY ----", StringComparison.Ordinal) ||
               normalizedKey.StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal);
    }
}
