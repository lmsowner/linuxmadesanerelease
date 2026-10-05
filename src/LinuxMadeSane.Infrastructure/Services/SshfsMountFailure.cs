// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Services;

internal static class SshfsMountFailure
{
    internal static string Describe(string source, string output, int exitCode)
    {
        bool Has(string value) => output.Contains(value, StringComparison.OrdinalIgnoreCase);
        var authenticated = Has("Authenticated to ");
        var explanation = output switch
        {
            _ when Has("REMOTE HOST IDENTIFICATION HAS CHANGED") || Has("Host key verification failed") =>
                "The server's SSH identity could not be verified. Verify its host key before updating the trusted SSH host key on this LMS server.",
            _ when Has("invalid format") || Has("error in libcrypto") || Has("UNPROTECTED PRIVATE KEY FILE") =>
                "SSH could not use the saved private key. Edit this host's saved credentials and test the key before retrying.",
            _ when Has("with partial success") =>
                "The server requires another authentication step after the key. SSHFS needs key-only login; review this user's SSH authentication policy before retrying.",
            _ when Has("Permission denied (password,keyboard-interactive)") || Has("Permission denied (password)") || Has("Permission denied (keyboard-interactive)") =>
                "The SSH server is not offering key-only login for this account. Repair the remote SSH authentication settings, then test the saved key and retry.",
            _ when !authenticated && !Has("fuse:") && !Has("fusermount") && (Has("Permission denied") || Has("No more authentication methods") || Has("sign_and_send_pubkey: signing failed")) =>
                "SSH login failed using the saved key. Test this host's saved credentials and check that its public key is authorized for this user.",
            _ when Has("subsystem request failed") =>
                "The server rejected SFTP access. SSHFS needs the SSH server's SFTP subsystem enabled for this user; terminal SSH access alone is insufficient.",
            _ when Has("Could not resolve hostname") =>
                "This LMS host could not resolve the server name. Check the saved hostname and DNS on the LMS host.",
            _ when Has("Connection refused") =>
                "The SSH connection was refused. Check the saved SSH port and that the server's SSH service is running.",
            _ when exitCode == 124 || Has("timed out") || Has("No route to host") || Has("Network is unreachable") =>
                "The SSH server could not be reached in time. Check connectivity and firewall rules from this LMS host to the saved server and port.",
            _ when Has("No such file or directory") && !Has("fuse:") =>
                "The requested remote folder or an SSHFS dependency was not found. Check the detail below and the remote path in this dialog.",
            _ when authenticated && Has("Permission denied") =>
                "SSH login succeeded, but access was denied while opening the remote folder. Check this user's folder permissions and SFTP restrictions.",
            _ when Has("fuse:") || Has("fusermount") =>
                "SSHFS could not create the local mount. Check the FUSE or mount-point error below on this LMS host.",
            _ when Has("Connection reset") || Has("Connection closed") => authenticated
                ? "SSH login succeeded, but the connection closed while opening SFTP or the remote folder. Check the server's SFTP restrictions and SSH logs for this user."
                : "The SSH connection was closed before LMS could confirm login. Check the server's SSH logs for this user and connections from this LMS host; the reset alone does not identify the cause.",
            _ => "SSHFS could not complete the mount. Review the server detail below before retrying."
        };

        // Keep actionable SSH/FUSE errors, without flooding the dialog with debug traces
        // containing local key paths, fingerprints and configuration files.
        var details = output.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 &&
                           !line.StartsWith("debug", StringComparison.OrdinalIgnoreCase) &&
                           !line.StartsWith("OpenSSH_", StringComparison.OrdinalIgnoreCase) &&
                           !line.StartsWith("SSHFS version", StringComparison.OrdinalIgnoreCase) &&
                           !line.StartsWith("executing <", StringComparison.OrdinalIgnoreCase) &&
                           !line.StartsWith("Authenticated to ", StringComparison.OrdinalIgnoreCase))
            .TakeLast(4);
        var detail = string.Join(" ", details);
        if (detail.Length > 800)
        {
            detail = detail[..800] + "…";
        }

        return $"Could not mount {source}. {explanation} " +
               (detail.Length == 0 ? $"SSHFS exit code: {exitCode}." : $"Server detail: {detail}");
    }
}
