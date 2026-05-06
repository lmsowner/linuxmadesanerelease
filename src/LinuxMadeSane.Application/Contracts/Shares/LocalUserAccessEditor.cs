using System.ComponentModel.DataAnnotations;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed class LocalUserAccessEditor
{
    [Required]
    public string UserName { get; set; } = string.Empty;

    public RemoteAccessSshAuthenticationMode SshAuthenticationMode { get; set; } = RemoteAccessSshAuthenticationMode.Password;

    public string AuthorizedKeyEntries { get; set; } = string.Empty;
}
