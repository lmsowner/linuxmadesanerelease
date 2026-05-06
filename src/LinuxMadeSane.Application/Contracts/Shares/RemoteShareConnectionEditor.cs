using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed class RemoteShareConnectionEditor
{
    [Required]
    public string Target { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string Domain { get; set; } = string.Empty;
}
