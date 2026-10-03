// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using System.Diagnostics;
using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SavedCredentialConnectionTransport(ManagedHostSshConnectionFactory factory) : ISavedCredentialConnectionTransport
{
    public async Task TestAsync(SavedConnectionCredentialEditor editor, CancellationToken token)
    {
        Validate(editor);
        if (editor.Kind == ConnectionCredentialKind.Smb) { await TestSmbAsync(editor, token); return; }
        var (host, credentials) = Build(editor);
        if (editor.Kind == ConnectionCredentialKind.Sftp)
        {
            using var client = factory.CreateSftpClient(host, credentials, TimeSpan.FromSeconds(10));
            client.OperationTimeout = TimeSpan.FromSeconds(10);
            await client.ConnectAsync(token);
            await Task.Run(() => client.GetAttributes(client.WorkingDirectory), token);
        }
        else
        {
            using var client = factory.CreateSshClient(host, credentials, TimeSpan.FromSeconds(10));
            await client.ConnectAsync(token);
        }
    }

    public async Task InstallPublicKeyAsync(SavedConnectionCredentialEditor editor, string publicKey, CancellationToken token)
    {
        Validate(editor);
        if (editor.Kind != ConnectionCredentialKind.Ssh) throw new InvalidOperationException("Use SSH password credentials to install a key.");
        var (host, credentials) = Build(editor);
        using var client = factory.CreateSshClient(host, credentials, TimeSpan.FromSeconds(10));
        await client.ConnectAsync(token);
        // Quote the key as one shell argument. Append only; preserve existing keys
        // and the server's password authentication policy.
        var key = "'" + publicKey.Replace("'", "'\\''") + "'";
        using var command = client.CreateCommand($"umask 077; mkdir -p \"$HOME/.ssh\" && chmod 700 \"$HOME/.ssh\" && touch \"$HOME/.ssh/authorized_keys\" && chmod 600 \"$HOME/.ssh/authorized_keys\" && (grep -qxF -- {key} \"$HOME/.ssh/authorized_keys\" || printf '\\n%s\\n' {key} >> \"$HOME/.ssh/authorized_keys\")");
        command.CommandTimeout = TimeSpan.FromSeconds(15);
        await command.ExecuteAsync(token);
        if (command.ExitStatus != 0) throw new InvalidOperationException("The remote account could not install the public key.");
    }

    private static void Validate(SavedConnectionCredentialEditor editor)
    {
        if (!Enum.IsDefined(editor.Kind) || string.IsNullOrWhiteSpace(editor.Server) || string.IsNullOrWhiteSpace(editor.Username) || editor.Port is < 1 or > 65535)
            throw new InvalidOperationException("Enter the server, username and port before testing.");
        if (editor.Server.StartsWith('-') || editor.Server.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\' or ','))
            throw new InvalidOperationException("Enter a hostname or IP address without a path.");
        if (editor.Kind == ConnectionCredentialKind.SshKeyPair ? string.IsNullOrWhiteSpace(editor.PrivateKey) : string.IsNullOrWhiteSpace(editor.Password))
            throw new InvalidOperationException("Enter or save the login credentials before testing.");
    }
    private static (ManagedHost, ManagedHostSshCredentials) Build(SavedConnectionCredentialEditor e)
    {
        var isKey = e.Kind == ConnectionCredentialKind.SshKeyPair;
        var auth = isKey ? AuthenticationType.PrivateKey : AuthenticationType.Password;
        return (new(Guid.NewGuid(), e.Name, e.Server, e.Port, "", "", "", HostOperatingStatus.Unknown,
            auth, null, e.Username, null, null, null, false, null, ConnectionTestStatus.NotRun, ""),
            new(e.Username, isKey ? null : e.Password, isKey ? e.PrivateKey : null, isKey ? e.Passphrase : null, auth));
    }
    private static async Task TestSmbAsync(SavedConnectionCredentialEditor e, CancellationToken token)
    {
        if ((e.Username + e.Password + e.Domain).Any(c => c is '\r' or '\n' or '\0'))
            throw new InvalidOperationException("SMB credentials cannot contain line breaks.");
        var directory = Path.Combine(Path.GetTempPath(), "lms-credential-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var path = Path.Combine(directory, "auth");
            await File.WriteAllTextAsync(path, $"username = {e.Username}\npassword = {e.Password}\ndomain = {e.Domain}\n", token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var start = new ProcessStartInfo("smbclient") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-g", "-L", e.Server, "-A", path, "-E" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start SMB test.");
            var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token);
            try { await process.WaitForExitAsync(token); }
            catch { if (!process.HasExited) process.Kill(true); throw; }
            await output; await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("SMB login failed or share browsing was refused. Check the server and credentials.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
