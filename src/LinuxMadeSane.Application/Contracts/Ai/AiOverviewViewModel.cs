// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
