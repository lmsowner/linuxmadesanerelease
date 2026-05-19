// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Core.Abstractions;

public interface IAiToolRegistry
{
    IReadOnlyList<AiToolDefinition> ListPublishedTools(
        AiChatThread thread,
        IReadOnlyList<AiAttachedServer> attachedServers);

    AiToolDefinition? FindTool(string toolName);
}
