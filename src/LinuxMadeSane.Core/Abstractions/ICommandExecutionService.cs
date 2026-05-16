// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ICommandExecutionService
{
    Task<CommandExecutionResult> ExecuteAsync(
        ManagedHost host,
        string commandText,
        IProgress<CommandExecutionUpdate>? progress = null,
        CancellationToken cancellationToken = default);
}
