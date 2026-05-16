// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.ComponentModel.DataAnnotations;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Application.Contracts.Sftp;

public sealed class SftpHostSettingsEditor : IValidatableObject
{
    public bool IsManagedModeEnabled { get; set; }

    [Required]
    public string BasePath { get; set; } = SftpServerDefaults.BasePath;

    public SftpAuthenticationMode DefaultAuthenticationMode { get; set; } = SftpAuthenticationMode.PublicKeyOnly;

    public bool PreferDropInConfiguration { get; set; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var normalizedPath = (BasePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            yield return new ValidationResult("Base path is required.", [nameof(BasePath)]);
            yield break;
        }

        if (!normalizedPath.StartsWith("/", StringComparison.Ordinal))
        {
            yield return new ValidationResult("Base path must be an absolute Linux path.", [nameof(BasePath)]);
        }

        if (normalizedPath.Equals("/", StringComparison.Ordinal))
        {
            yield return new ValidationResult("Base path cannot be the filesystem root.", [nameof(BasePath)]);
        }
    }
}
