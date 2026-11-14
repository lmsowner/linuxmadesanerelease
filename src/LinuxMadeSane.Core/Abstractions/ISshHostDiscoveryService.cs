// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISshHostDiscoveryService
{
    Task<IReadOnlyList<LinuxMadeSane.Core.Models.RemoteDesktop.RemoteDesktopCandidate>> DiscoverRemoteDesktopsAsync(
        SshHostDiscoveryScope scope, IProgress<string>? progress = null, CancellationToken token = default) =>
        Task.FromException<IReadOnlyList<LinuxMadeSane.Core.Models.RemoteDesktop.RemoteDesktopCandidate>>(new NotSupportedException());

    Task<IReadOnlyList<LinuxMadeSane.Core.Models.RemoteDesktop.RemoteDesktopCandidate>> DiscoverRemoteDesktopsAsync(
        SshHostDiscoveryScope scope, IProgress<string>? progress,
        IProgress<LinuxMadeSane.Core.Models.RemoteDesktop.RemoteDesktopProbeResult>? results,
        CancellationToken token) => DiscoverRemoteDesktopsAsync(scope, progress, token);

    Task<string?> ResolveRemoteDesktopDnsNameAsync(string address, CancellationToken token = default) => Task.FromResult<string?>(null);

    Task<SshHostDiscoveryResult> GetCachedHostsAsync(
        SshHostDiscoveryScope scope,
        CancellationToken cancellationToken = default);

    Task<SshHostDiscoveryResult> DiscoverHostsAsync(
        SshHostDiscoveryScope scope,
        IProgress<SshHostDiscoveryProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default);
}
