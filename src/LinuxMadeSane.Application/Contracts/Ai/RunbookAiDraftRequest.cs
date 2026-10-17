// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record RunbookAiDraftRequest(
    RunbookEditor Editor,
    IReadOnlyList<ManagedHost> Hosts,
    string UserPrompt,
    string ProviderKey,
    string ModelId,
    IReadOnlyList<AiChatMessage>? MessageHistory = null);
