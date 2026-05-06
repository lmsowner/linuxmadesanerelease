using System.ComponentModel.DataAnnotations;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed class SecurityUserEditor
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    public string LinuxUsername { get; set; } = string.Empty;

    public RemoteAccessSshAuthenticationMode SshAuthenticationMode { get; set; } = RemoteAccessSshAuthenticationMode.Password;

    public string AuthorizedKeyEntries { get; set; } = string.Empty;
}
