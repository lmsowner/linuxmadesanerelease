// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
