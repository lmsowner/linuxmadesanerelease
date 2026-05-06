using System.ComponentModel.DataAnnotations;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Sftp;

public sealed class SftpPasswordResetEditor : IValidatableObject
{
    public string UserName { get; set; } = string.Empty;

    public SftpAuthenticationMode AuthenticationMode { get; set; }

    [Required]
    public string NewPassword { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AuthenticationMode == SftpAuthenticationMode.PublicKeyOnly)
        {
            yield return new ValidationResult(
                "Password reset is not available while the user is in PublicKeyOnly mode.",
                [nameof(AuthenticationMode)]);
        }

        if (string.IsNullOrWhiteSpace(NewPassword))
        {
            yield return new ValidationResult("Enter a new password.", [nameof(NewPassword)]);
        }
    }
}
