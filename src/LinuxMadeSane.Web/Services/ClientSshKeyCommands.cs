// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Security.Cryptography;
using System.Text;

namespace LinuxMadeSane.Web.Services;

public static class ClientSshKeyCommands
{
    public static string FileName(string machine, string user)
    {
        var identity = machine.Trim().ToLowerInvariant() + "\0" + user.Trim();
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..8].ToLowerInvariant();
        return $"id_ed25519_lms_{Slug(machine, "server")}_{Slug(user, "user")}_{suffix}";
    }

    private static string Slug(string value, string fallback)
    {
        var slug = new string(value.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').Take(40).ToArray());
        return string.IsNullOrWhiteSpace(slug) ? fallback : slug;
    }

    public static string PrivateKeyPath(string machine, string user, bool windows) => windows
        ? $"\"$env:USERPROFILE\\.ssh\\{FileName(machine, user)}\""
        : $"\"$HOME/.ssh/{FileName(machine, user)}\"";

    public static string Generate(string machine, string user, bool windows)
    {
        var keyPath = PrivateKeyPath(machine, user, windows);
        return windows
            ? "New-Item -ItemType Directory -Force \"$env:USERPROFILE\\.ssh\" | Out-Null\n" +
              $"$lmsKeyPath = {keyPath}\n" +
              "if ((Test-Path -LiteralPath $lmsKeyPath) -or (Test-Path -LiteralPath \"$lmsKeyPath.pub\")) {\n" +
              "    Write-Host 'Existing key files were preserved.'\n" +
              "} else {\n    ssh-keygen -t ed25519 -a 64 -f $lmsKeyPath\n}\n" +
              "if ((Test-Path -LiteralPath $lmsKeyPath -PathType Leaf) -and (Test-Path -LiteralPath \"$lmsKeyPath.pub\" -PathType Leaf)) {\n" +
              "    Get-Content -LiteralPath \"$lmsKeyPath.pub\"\n} else {\n" +
              "    Write-Error 'No complete key pair is available. Keep existing files and choose another filename or your existing matching key.'\n}"
            : "mkdir -p \"$HOME/.ssh\"\n" +
              $"lms_key={keyPath}\n" +
              "if [ -e \"$lms_key\" ] || [ -L \"$lms_key\" ] || [ -e \"$lms_key.pub\" ] || [ -L \"$lms_key.pub\" ]; then\n" +
              "    printf '%s\\n' 'Existing key files were preserved.'\n" +
              "else\n    ssh-keygen -t ed25519 -a 64 -f \"$lms_key\"\nfi\n" +
              "if [ -f \"$lms_key\" ] && [ -f \"$lms_key.pub\" ]; then\n    cat \"$lms_key.pub\"\nelse\n" +
              "    printf '%s\\n' 'No complete key pair is available. Keep existing files and choose another filename or your existing matching key.' >&2\n    false\nfi";
    }
}
