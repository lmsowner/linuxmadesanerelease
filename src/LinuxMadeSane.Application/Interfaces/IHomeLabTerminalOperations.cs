// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Interfaces;

// The terminal agent authorizes changes before invoking this local managed executor.
public interface IHomeLabTerminalOperations
{
    Task<AiToolExecutionResult> ExecuteAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default);
}
