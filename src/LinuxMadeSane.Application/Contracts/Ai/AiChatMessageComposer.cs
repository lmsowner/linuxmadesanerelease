// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed class AiChatMessageComposer
{
    [Required]
    [StringLength(8000)]
    public string Content { get; set; } = string.Empty;
}
