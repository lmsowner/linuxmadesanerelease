using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SshKeyPairGenerator : ISshKeyPairGenerator
{
    public async Task<GeneratedSshKeyPair> GenerateAsync(
        SshKeyGenerationProfile profile = SshKeyGenerationProfile.Ed25519,
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedComment = NormalizeComment(comment);

        if (profile == SshKeyGenerationProfile.Ed25519)
        {
            return await GenerateEd25519Async(normalizedComment, cancellationToken);
        }

        var keySize = ResolveRsaKeySize(profile);
        using var rsa = RSA.Create();
        rsa.KeySize = keySize;

        var publicParameters = rsa.ExportParameters(false);
        var publicKeyBlob = BuildPublicKeyBlob(publicParameters);
        var publicKey = normalizedComment.Length == 0
            ? $"ssh-rsa {Convert.ToBase64String(publicKeyBlob)}"
            : $"ssh-rsa {Convert.ToBase64String(publicKeyBlob)} {normalizedComment}";

        var fingerprint = BuildFingerprint(publicKeyBlob);

        var privateKey = rsa.ExportRSAPrivateKeyPem();
        ValidateGeneratedPrivateKey(privateKey);

        return new GeneratedSshKeyPair(
            $"RSA-{keySize}",
            privateKey,
            publicKey,
            fingerprint);
    }

    private static async Task<GeneratedSshKeyPair> GenerateEd25519Async(
        string normalizedComment,
        CancellationToken cancellationToken)
    {
        var tempDirectoryPath = Path.Combine(Path.GetTempPath(), $"lms-ssh-keygen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectoryPath);
        var privateKeyPath = Path.Combine(tempDirectoryPath, "id_ed25519");

        try
        {
            using var process = new Process
            {
                StartInfo = BuildEd25519StartInfo(privateKeyPath, normalizedComment)
            };

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("Failed to start ssh-keygen for Ed25519 key generation.");
                }
            }
            catch (Win32Exception exception)
            {
                throw new InvalidOperationException(
                    "Ed25519 key generation requires ssh-keygen to be installed on the Linux Made Sane host.",
                    exception);
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch
            {
                TryKill(process);
                throw;
            }

            var standardOutput = await process.StandardOutput.ReadToEndAsync();
            var standardError = await process.StandardError.ReadToEndAsync();
            if (process.ExitCode != 0)
            {
                var details = !string.IsNullOrWhiteSpace(standardError)
                    ? standardError.Trim()
                    : string.IsNullOrWhiteSpace(standardOutput)
                        ? "ssh-keygen exited with a non-zero status."
                        : standardOutput.Trim();
                throw new InvalidOperationException($"ssh-keygen failed to generate an Ed25519 key: {details}");
            }

            var privateKey = await File.ReadAllTextAsync(privateKeyPath, cancellationToken);
            var publicKey = (await File.ReadAllTextAsync($"{privateKeyPath}.pub", cancellationToken)).Trim();

            ValidateGeneratedPrivateKey(privateKey);

            return new GeneratedSshKeyPair(
                "Ed25519",
                privateKey,
                publicKey,
                BuildFingerprintFromPublicKey(publicKey));
        }
        finally
        {
            TryDeleteDirectory(tempDirectoryPath);
        }
    }

    private static int ResolveRsaKeySize(SshKeyGenerationProfile profile) => profile switch
    {
        SshKeyGenerationProfile.Rsa2048 => 2048,
        SshKeyGenerationProfile.Rsa3072 => 3072,
        SshKeyGenerationProfile.Rsa4096 => 4096,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unsupported RSA key generation profile.")
    };

    private static byte[] BuildPublicKeyBlob(RSAParameters parameters)
    {
        using var stream = new MemoryStream();
        WriteSshString(stream, Encoding.ASCII.GetBytes("ssh-rsa"));
        WriteSshString(stream, NormalizeMpint(parameters.Exponent));
        WriteSshString(stream, NormalizeMpint(parameters.Modulus));
        return stream.ToArray();
    }

    private static void WriteSshString(Stream stream, byte[] data)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)data.Length);
        stream.Write(lengthBytes);
        stream.Write(data, 0, data.Length);
    }

    private static byte[] NormalizeMpint(byte[]? data)
    {
        if (data is null || data.Length == 0)
        {
            return [];
        }

        var offset = 0;
        while (offset < data.Length && data[offset] == 0)
        {
            offset++;
        }

        var trimmed = offset == 0 ? data : data[offset..];
        if (trimmed.Length == 0)
        {
            return [];
        }

        if ((trimmed[0] & 0x80) == 0)
        {
            return trimmed;
        }

        var withLeadingZero = new byte[trimmed.Length + 1];
        Buffer.BlockCopy(trimmed, 0, withLeadingZero, 1, trimmed.Length);
        return withLeadingZero;
    }

    private static ProcessStartInfo BuildEd25519StartInfo(string privateKeyPath, string normalizedComment)
    {
        var startInfo = new ProcessStartInfo("ssh-keygen")
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("-q");
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("ed25519");
        startInfo.ArgumentList.Add("-N");
        startInfo.ArgumentList.Add(string.Empty);
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(normalizedComment);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(privateKeyPath);
        return startInfo;
    }

    private static string BuildFingerprintFromPublicKey(string publicKey)
    {
        var parts = publicKey.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new InvalidOperationException("ssh-keygen produced an invalid SSH public key.");
        }

        return BuildFingerprint(Convert.FromBase64String(parts[1]));
    }

    private static string BuildFingerprint(byte[] publicKeyBlob) =>
        $"SHA256:{Convert.ToBase64String(SHA256.HashData(publicKeyBlob)).TrimEnd('=')}";

    private static string NormalizeComment(string? comment) =>
        string.IsNullOrWhiteSpace(comment)
            ? string.Empty
            : string.Join(' ', comment.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));

    private static void ValidateGeneratedPrivateKey(string privateKey)
    {
        if (SshKeyMaterialClassifier.LooksLikePublicKey(privateKey))
        {
            throw new InvalidOperationException("SSH key generation returned a public key where a private key was expected.");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
