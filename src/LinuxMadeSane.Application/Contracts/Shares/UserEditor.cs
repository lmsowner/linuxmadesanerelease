using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed class UserEditor
{
    public Guid? Id { get; set; }

    [Required]
    public string UserName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string PrimaryGroup { get; set; } = string.Empty;

    public string SupplementaryGroupsCsv { get; set; } = string.Empty;

    public string HomeDirectory { get; set; } = string.Empty;

    public string LoginShell { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;
}
