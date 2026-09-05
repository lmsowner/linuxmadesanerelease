// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models;

public sealed record TerminalAiCommandRequest(
    Guid RequestId,
    Guid TerminalSessionId,
    string CommandText,
    string WorkingDirectory,
    CommandExecutionInput? StandardInput = null,
    int TimeoutSeconds = 120);
