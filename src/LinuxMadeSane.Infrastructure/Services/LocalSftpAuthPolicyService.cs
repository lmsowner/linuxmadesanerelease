// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalSftpAuthPolicyService : ISftpAuthPolicyService
{
    private static readonly IReadOnlySet<string> AllowedKeyTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "ssh-ed25519",
        "ssh-rsa",
        "ecdsa-sha2-nistp256",
        "ecdsa-sha2-nistp384",
        "ecdsa-sha2-nistp521",
        "sk-ssh-ed25519@openssh.com",
        "sk-ecdsa-sha2-nistp256@openssh.com"
    };

    public IReadOnlyList<string> GetSupplementaryGroups(SftpAuthenticationMode mode) =>
        mode switch
        {
            SftpAuthenticationMode.PasswordOnly => [SftpServerDefaults.PasswordOnlyGroup],
            SftpAuthenticationMode.PublicKeyOnly => [SftpServerDefaults.PublicKeyOnlyGroup],
            SftpAuthenticationMode.PasswordAndPublicKey => [SftpServerDefaults.PublicKeyAndPasswordGroup],
            _ => [SftpServerDefaults.PublicKeyOnlyGroup]
        };

    public string NormalizePublicKey(string publicKeyText) =>
        ParsePublicKey(publicKeyText).CanonicalText;

    public string BuildAuthorizedKeysFileContents(IReadOnlyList<string> publicKeys) =>
        string.Join(
            Environment.NewLine,
            publicKeys
                .Where(static key => !string.IsNullOrWhiteSpace(key))
                .Select(NormalizePublicKey)
                .Distinct(StringComparer.Ordinal)) + Environment.NewLine;

    public void ValidatePublicKey(string publicKeyText)
    {
        _ = ParsePublicKey(publicKeyText);
    }

    public string BuildFingerprint(string publicKeyText)
    {
        var parsed = ParsePublicKey(publicKeyText);
        var hash = SHA256.HashData(parsed.Blob);
        return $"SHA256:{Convert.ToBase64String(hash).TrimEnd('=')}";
    }

    private ParsedPublicKey ParsePublicKey(string publicKeyText)
    {
        var trimmed = publicKeyText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("Enter a valid OpenSSH or SSH2 public key.");
        }

        return trimmed.StartsWith("---- BEGIN SSH2 PUBLIC KEY ----", StringComparison.Ordinal)
            ? ParseSsh2PublicKey(trimmed)
            : ParseOpenSshPublicKey(trimmed);
    }

    private ParsedPublicKey ParseOpenSshPublicKey(string text)
    {
        var parts = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            throw new InvalidOperationException("Enter a valid OpenSSH or SSH2 public key.");
        }

        var body = DecodeBase64Body(parts[1]);
        var derivedKeyType = ReadKeyType(body);
        if (!AllowedKeyTypes.Contains(derivedKeyType))
        {
            throw new InvalidOperationException($"'{derivedKeyType}' is not a supported SSH public key type.");
        }

        if (!string.Equals(parts[0], derivedKeyType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The SSH public key type does not match the encoded key body.");
        }

        var comment = parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2].Trim() : null;
        return new ParsedPublicKey(derivedKeyType, parts[1], comment, body);
    }

    private ParsedPublicKey ParseSsh2PublicKey(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 3 ||
            !string.Equals(lines[0], "---- BEGIN SSH2 PUBLIC KEY ----", StringComparison.Ordinal) ||
            !string.Equals(lines[^1], "---- END SSH2 PUBLIC KEY ----", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Enter a valid SSH2 public key block.");
        }

        var base64Builder = new StringBuilder();
        string? comment = null;
        for (var index = 1; index < lines.Length - 1; index++)
        {
            var line = lines[index].Trim();
            if (line.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase))
            {
                comment = NormalizeSsh2Comment(line["Comment:".Length..].Trim());
                continue;
            }

            if (line.Contains(':', StringComparison.Ordinal))
            {
                continue;
            }

            base64Builder.Append(line.TrimEnd('\\'));
        }

        var encodedBody = base64Builder.ToString();
        var body = DecodeBase64Body(encodedBody);
        var keyType = ReadKeyType(body);
        if (!AllowedKeyTypes.Contains(keyType))
        {
            throw new InvalidOperationException($"'{keyType}' is not a supported SSH public key type.");
        }

        return new ParsedPublicKey(keyType, encodedBody, comment, body);
    }

    private static byte[] DecodeBase64Body(string value)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("The SSH public key body is not valid base64.");
        }
    }

    private static string ReadKeyType(byte[] publicKeyBlob)
    {
        if (publicKeyBlob.Length < 4)
        {
            throw new InvalidOperationException("The SSH public key body is incomplete.");
        }

        var stringLength = BinaryPrimitives.ReadInt32BigEndian(publicKeyBlob.AsSpan(0, 4));
        if (stringLength <= 0 || publicKeyBlob.Length < 4 + stringLength)
        {
            throw new InvalidOperationException("The SSH public key body is malformed.");
        }

        return Encoding.ASCII.GetString(publicKeyBlob, 4, stringLength);
    }

    private static string? NormalizeSsh2Comment(string comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return null;
        }

        var trimmed = comment.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }

    private sealed record ParsedPublicKey(string KeyType, string EncodedBody, string? Comment, byte[] Blob)
    {
        public string CanonicalText =>
            string.IsNullOrWhiteSpace(Comment)
                ? $"{KeyType} {EncodedBody}"
                : $"{KeyType} {EncodedBody} {Comment}";
    }
}
