// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed class AiChatMessageComposer
{
    [Required]
    [StringLength(8000)]
    public string Content { get; set; } = string.Empty;
}
