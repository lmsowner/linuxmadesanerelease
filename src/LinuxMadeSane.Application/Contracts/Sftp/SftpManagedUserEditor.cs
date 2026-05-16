// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Sftp;

public sealed class SftpManagedUserEditor : IValidatableObject
{
    private static readonly Regex UserNameRegex = new("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.Compiled);

    public string OriginalUserName { get; set; } = string.Empty;

    [Required]
    public string UserName { get; set; } = string.Empty;

    public SftpAuthenticationMode AuthenticationMode { get; set; } = SftpAuthenticationMode.PublicKeyOnly;

    public bool IsEnabled { get; set; } = true;

    public bool HasExistingPassword { get; set; }

    public DateTimeOffset? PasswordChangedAtUtc { get; set; }

    public List<SftpPublicKeyEditor> PublicKeys { get; set; } = [];

    public bool IsNewUser =>
        string.IsNullOrWhiteSpace(OriginalUserName);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var normalizedUserName = (UserName ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedUserName))
        {
            yield return new ValidationResult("Username is required.", [nameof(UserName)]);
            yield break;
        }

        if (!UserNameRegex.IsMatch(normalizedUserName))
        {
            yield return new ValidationResult(
                "Use a lowercase Linux username starting with a letter or underscore, then only lowercase letters, digits, hyphens, or underscores.",
                [nameof(UserName)]);
        }

        if (AuthenticationMode != SftpAuthenticationMode.PasswordOnly &&
            PublicKeys.All(static key => string.IsNullOrWhiteSpace(key.PublicKeyText)))
        {
            yield return new ValidationResult(
                "Add at least one SSH public key for key-based authentication modes.",
                [nameof(PublicKeys)]);
        }
    }
}
