using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed class TrustedNetworkEntryEditor
{
    public Guid? Id { get; set; }

    [Required]
    public string Label { get; set; } = string.Empty;

    [Required]
    public string AddressOrCidr { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public bool IsTrustedAccessEnabled { get; set; } = true;

    public bool IsAuthenticationEnabled { get; set; } = true;
}
