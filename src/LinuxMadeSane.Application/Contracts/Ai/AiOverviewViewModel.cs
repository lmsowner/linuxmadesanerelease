// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record AiOverviewViewModel(
    int ThreadCount,
    int AttachedServerCount,
    int PendingApprovalCount,
    int AuditEntryCount,
    int CheckpointCount,
    int SupportedProviderCount,
    int ConfiguredProviderCount,
    IReadOnlyList<AiProviderDefinition> SupportedProviders,
    IReadOnlyList<AiConfiguredProviderViewModel> ConfiguredProviders);
