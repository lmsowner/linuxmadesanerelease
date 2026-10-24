// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Core.Abstractions;

public interface IAiToolRegistry
{
    IReadOnlyList<AiToolDefinition> ListPublishedTools(
        AiChatThread thread,
        IReadOnlyList<AiAttachedServer> attachedServers);

    AiToolDefinition? FindTool(string toolName);
}
