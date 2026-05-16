// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Application.Contracts.LocalAi;

public sealed class LocalAiSharingEditor
{
    public bool SharingEnabled { get; set; }
    public bool AllowOrganizationInstances { get; set; } = true;

    [Range(1, 32)]
    public int MaxConcurrentRequests { get; set; } = 2;

    [Range(0, 128)]
    public int MaxQueuedRequests { get; set; } = 4;

    [Range(1, 1000)]
    public int MaxRequestsPerMinute { get; set; } = 30;

    [Range(512, 200000)]
    public int MaxPromptCharacters { get; set; } = 24000;

    [Range(10, 3600)]
    public int RequestTimeoutSeconds { get; set; } = 120;

    public string AllowedInstanceIdsText { get; set; } = string.Empty;
    public string AllowedOrganizationIdsText { get; set; } = string.Empty;
    public string AllowedModelIdsText { get; set; } = string.Empty;
}
