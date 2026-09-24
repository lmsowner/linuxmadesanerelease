// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed class AiChatThreadEditor
{
    public Guid? Id { get; set; }

    [Required]
    public string Title { get; set; } = "Untitled AI chat";

    public string ProviderKey { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;

    public AiTrustLevel TrustLevel { get; set; } = AiTrustLevel.Guided;

    public bool AllowReadOnlyTools { get; set; } = true;

    public bool AllowMutatingTools { get; set; }

    public bool RequireApprovalForMediumRisk { get; set; } = true;

    public bool RequireApprovalForHighRisk { get; set; } = true;

    public List<Guid> AttachedServerIds { get; set; } = [];
}
