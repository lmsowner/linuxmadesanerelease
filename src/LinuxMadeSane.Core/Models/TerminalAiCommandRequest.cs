// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record TerminalAiCommandRequest(
    Guid RequestId,
    Guid TerminalSessionId,
    string CommandText,
    string WorkingDirectory,
    CommandExecutionInput? StandardInput = null,
    int TimeoutSeconds = 120,
    bool AuthenticateSudo = false);
