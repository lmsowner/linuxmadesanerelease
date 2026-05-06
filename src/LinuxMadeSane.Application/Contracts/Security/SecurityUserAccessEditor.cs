using System.ComponentModel.DataAnnotations;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed class SecurityUserAccessEditor
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;

    [Required]
    public string LinuxUsername { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }
    public RemoteAccessSshAuthenticationMode SshAuthenticationMode { get; set; } = RemoteAccessSshAuthenticationMode.Password;
    public string AuthorizedKeyEntries { get; set; } = string.Empty;
}
