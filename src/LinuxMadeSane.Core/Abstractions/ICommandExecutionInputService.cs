// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ICommandExecutionInputService
{
    Task<CommandExecutionResult> ExecuteAsync(
        ManagedHost host,
        string commandText,
        CommandExecutionInput input,
        IProgress<CommandExecutionUpdate>? progress = null,
        CancellationToken cancellationToken = default);
}
