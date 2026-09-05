// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ITerminalAiCommandDaemon
{
    Task<TerminalAiCommandResult> ExecuteAsync(
        TerminalAiCommandRequest request,
        IProgress<CommandExecutionUpdate>? progress = null,
        CancellationToken cancellationToken = default);
}
