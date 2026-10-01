// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Interfaces;

// The terminal agent authorizes changes before invoking this local managed executor.
public interface IHomeLabTerminalOperations
{
    Task<AiToolExecutionResult> ExecuteAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default);
}
