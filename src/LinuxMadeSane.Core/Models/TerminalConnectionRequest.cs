namespace LinuxMadeSane.Core.Models;

public sealed record TerminalConnectionRequest(
    string Username,
    string? Password,
    string? PrivateKey,
    string? PrivateKeyPassphrase,
    bool PreferStoredCredentials,
    int Columns,
    int Rows,
    string? WorkingDirectory = null);
