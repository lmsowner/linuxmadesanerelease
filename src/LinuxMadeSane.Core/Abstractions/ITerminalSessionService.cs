// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ITerminalSessionService
{
    event Action<TerminalSessionOutputAppended>? OutputAppended;

    Task<TerminalSession> StartSessionAsync(
        ManagedHost host,
        TerminalConnectionRequest request,
        CancellationToken cancellationToken = default);

    Task<TerminalSessionSnapshot?> GetSnapshotAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task SendInputAsync(
        Guid sessionId,
        string input,
        CancellationToken cancellationToken = default);

    Task SendInterruptAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        SendInputAsync(sessionId, "\u0003", cancellationToken);

    Task<TerminalAiCommandResult> ExecuteAiCommandAsync(
        TerminalAiCommandRequest request,
        IProgress<CommandExecutionUpdate>? progress = null,
        CancellationToken cancellationToken = default);

    Task ResizeAsync(
        Guid sessionId,
        int columns,
        int rows,
        CancellationToken cancellationToken = default);

    Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task CloseOwnedSessionsAsync(Guid ownerId, CancellationToken cancellationToken = default);
}
