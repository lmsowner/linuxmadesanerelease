// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

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

    Task ResizeAsync(
        Guid sessionId,
        int columns,
        int rows,
        CancellationToken cancellationToken = default);

    Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
