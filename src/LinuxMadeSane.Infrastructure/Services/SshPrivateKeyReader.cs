// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Utilities;
using LinuxMadeSane.Core.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace LinuxMadeSane.Infrastructure.Services;

public static class SshPrivateKeyReader
{
    internal static string PublicKey(PrivateKeyFile key) => FormatPublicKey(key.HostKeyAlgorithms.First().Data);

    private static string FormatPublicKey(byte[] blob)
    {
        var nameLength = BinaryPrimitives.ReadInt32BigEndian(blob);
        return Encoding.ASCII.GetString(blob, 4, nameLength) + " " + Convert.ToBase64String(blob);
    }

    public static string PreviewPublicKey(string text, string? passphrase)
    {
        var normalized = Normalize(text);
        if (normalized.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----", StringComparison.Ordinal) &&
            EncryptedOpenSshPublicKey(normalized) is {} identity)
            return identity;

        using var key = Open(normalized, passphrase);
        return PublicKey(key);
    }

    // OpenSSH includes the public identity outside the encrypted private section.
    // Verify the public blob's structure before copying any data into a visible field.
    private static string? EncryptedOpenSshPublicKey(string text)
    {
        if (!PemEncoding.TryFind(text, out var pem)) return null;
        byte[] bytes = Convert.FromBase64String(text[pem.Base64Data]);
        try
        {
            ReadOnlySpan<byte> packet = bytes;
            if (!packet.StartsWith("openssh-key-v1\0"u8)) return null;
            packet = packet["openssh-key-v1\0"u8.Length..];
            if (TakeString(ref packet).SequenceEqual("none"u8)) return null;
            TakeString(ref packet); // KDF name
            TakeString(ref packet); // KDF options
            if (packet.Length < 4 || BinaryPrimitives.ReadUInt32BigEndian(packet) != 1) return null;
            packet = packet[4..];
            var publicBlob = TakeString(ref packet);
            if (TakeString(ref packet).IsEmpty || !packet.IsEmpty) return null;
            var publicKey = OpenSshPublicKeyUtilities.ParsePublicKey(publicBlob.ToArray());
            var canonical = OpenSshPublicKeyUtilities.EncodePublicKey(publicKey);
            return canonical.AsSpan().SequenceEqual(publicBlob) ? FormatPublicKey(canonical) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or ArithmeticException or FormatException)
        {
            return null;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static ReadOnlySpan<byte> TakeString(ref ReadOnlySpan<byte> packet)
    {
        if (packet.Length < sizeof(int)) throw new FormatException("Incomplete OpenSSH key.");
        int length = BinaryPrimitives.ReadInt32BigEndian(packet);
        if (length < 0 || length > packet.Length - sizeof(int)) throw new FormatException("Incomplete OpenSSH key.");
        var field = packet.Slice(sizeof(int), length);
        packet = packet[(sizeof(int) + length)..];
        return field;
    }

    private static string Normalize(string text)
    {
        if (SshKeyMaterialClassifier.LooksLikePublicKey(text))
            throw new InvalidOperationException("This is a public key. Choose the matching private key file.");
        try { return SshPrivateKeyText.Normalize(text) ?? text.Trim(); }
        catch (FormatException ex) { throw new InvalidOperationException(ex.Message); }
    }

    internal static PrivateKeyFile Read(string text, string? passphrase, out string normalized)
    {
        normalized = Normalize(text);
        return Open(normalized, passphrase);
    }

    private static PrivateKeyFile Open(string normalized, string? passphrase)
    {
        if (string.IsNullOrEmpty(passphrase) && normalized.StartsWith("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal))
            throw new InvalidOperationException("This private key is encrypted. Enter its key passphrase.");
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(normalized));
            return new PrivateKeyFile(stream, passphrase);
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            throw new InvalidOperationException("This private key is encrypted. Enter its key passphrase.");
        }
        catch (Exception ex) when (ex is SshException or FormatException or ArgumentException or CryptographicException
            or InvalidOperationException or NotSupportedException or IOException or Org.BouncyCastle.Crypto.CryptoException or Org.BouncyCastle.Pkcs.PkcsException)
        {
            throw new InvalidOperationException(string.IsNullOrEmpty(passphrase)
                ? "LMS could not read the key data. Choose the original private key file or paste the complete key."
                : "LMS could not open this private key. Check the key passphrase and that the complete key was copied.");
        }
    }
}
