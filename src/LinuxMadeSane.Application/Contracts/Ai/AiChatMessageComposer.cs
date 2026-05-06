using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed class AiChatMessageComposer
{
    [Required]
    [StringLength(8000)]
    public string Content { get; set; } = string.Empty;
}
