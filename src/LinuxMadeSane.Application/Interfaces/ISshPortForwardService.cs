// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Application.Contracts.Security;
namespace LinuxMadeSane.Application.Interfaces;
public interface ISshPortForwardService
{
    Task<IReadOnlyList<SshForwardView>> ListAsync(CancellationToken token = default);
    Task TestConnectionAsync(SshPortForward forward, CancellationToken token = default);
    Task SaveAsync(SshPortForward forward, CancellationToken token = default);
    Task SetEnabledAsync(Guid id, bool enabled, CancellationToken token = default);
    Task RestartAsync(Guid id, CancellationToken token = default);
    Task DeleteAsync(Guid id, CancellationToken token = default);
    Task LinkCaddyAsync(Guid id, SshForwardCaddyLink link, CancellationToken token = default);
    Task UnlinkCaddyAsync(Guid id, CancellationToken token = default);
}
