// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Contracts.DesktopAssistant;

public sealed record DesktopAssistantChatWorkspaceViewModel(
    IReadOnlyList<DesktopAssistantChatSessionViewModel> Sessions,
    Guid? ActiveSessionId,
    IReadOnlyList<AiChatMessage> Messages,
    bool IsReady,
    bool HasProvider,
    string ActiveProviderKey,
    string ProviderLabel,
    string ModelId,
    string StatusSummary,
    DesktopAssistantProposedFixViewModel? ProposedFix = null);

public sealed record DesktopAssistantProposedFixViewModel(
    string Kind,
    IReadOnlyDictionary<string, string> Arguments,
    string Title,
    string Description);
