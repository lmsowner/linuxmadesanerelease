// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Models.RemoteDesktop;
namespace LinuxMadeSane.Core.Abstractions;
public interface IRemoteDesktopConnectionStore
{
    Task<IReadOnlyList<RemoteDesktopConnection>> ListAsync(Guid owner, CancellationToken token = default);
    Task<RemoteDesktopConnection?> GetAsync(Guid id, CancellationToken token = default);
    Task SaveAsync(RemoteDesktopConnection connection, CancellationToken token = default);
    Task DeleteAsync(Guid id, CancellationToken token = default);
}
