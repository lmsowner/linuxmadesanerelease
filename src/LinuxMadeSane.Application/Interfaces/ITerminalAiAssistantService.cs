// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Ai;

namespace LinuxMadeSane.Application.Interfaces;

public interface ITerminalAiAssistantService
{
    Task<TerminalAiTurnResult> ExecutePromptAsync(
        TerminalAiConversationState conversation,
        TerminalAiPromptRequest request,
        CancellationToken cancellationToken = default);
}
