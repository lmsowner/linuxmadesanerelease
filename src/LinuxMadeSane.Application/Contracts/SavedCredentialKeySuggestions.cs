// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Application.Contracts;

public static class SavedCredentialKeySuggestions
{
    public static bool Matches(SavedConnectionCredentialSummary key, SavedConnectionCredentialEditor target) =>
        target.Kind == ConnectionCredentialKind.Ssh && !string.IsNullOrWhiteSpace(target.Server) && !string.IsNullOrWhiteSpace(target.Username) &&
        key.Kind == ConnectionCredentialKind.SshKeyPair && key.HasPrivateKey &&
        (string.IsNullOrWhiteSpace(key.Server) || (string.Equals(key.Server.Trim(), target.Server.Trim(), StringComparison.OrdinalIgnoreCase) && key.Port == target.Port)) &&
        (string.IsNullOrWhiteSpace(key.Username) || string.Equals(key.Username.Trim(), target.Username.Trim(), StringComparison.Ordinal));
}
