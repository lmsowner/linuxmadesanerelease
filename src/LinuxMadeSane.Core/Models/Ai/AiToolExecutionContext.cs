// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiToolExecutionContext(
    AiToolInvocation Invocation,
    AiChatThread Thread,
    IReadOnlyList<AiAttachedServer> AttachedServers);
