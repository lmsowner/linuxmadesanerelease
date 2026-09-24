// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Shares;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Shares;
using Microsoft.Extensions.DependencyInjection;

namespace LinuxMadeSane.Web.Services;

public sealed class ShareMountsWorkspaceService(IServiceScopeFactory scopeFactory)
{
    public Task<NetworkShareMountExplorerViewModel> GetNetworkShareMountExplorerAsync(
        NetworkShareDiscoveryScope discoveryScope,
        bool runDiscovery = true,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            (service, token) => service.GetNetworkShareMountExplorerAsync(discoveryScope, runDiscovery, token),
            cancellationToken);

    public Task<SshfsMountExplorerViewModel> GetSshfsMountExplorerAsync(
        bool inspectTooling = true,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            (service, token) => service.GetSshfsMountExplorerAsync(inspectTooling, token),
            cancellationToken);

    public Task<int> GetShareCountAsync(CancellationToken cancellationToken = default) =>
        RunAsync((service, token) => service.GetShareCountAsync(token), cancellationToken);

    public Task<ShareMountReconnectSummary> ReconnectDisconnectedManagedMountsAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync((service, token) => service.ReconnectDisconnectedManagedMountsAsync(token), cancellationToken);

    public Task<RemoteShareMountResult?> ReconnectManagedRemoteMountAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        RunAsync((service, token) => service.ReconnectManagedRemoteMountAsync(id, token), cancellationToken);

    public Task<SshfsMountResult?> ReconnectManagedSshfsMountAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        RunAsync((service, token) => service.ReconnectManagedSshfsMountAsync(id, token), cancellationToken);

    public Task<RemoteShareBrowseResult> BrowseRemoteSharesAsync(
        RemoteShareConnectionEditor editor,
        CancellationToken cancellationToken = default) =>
        RunAsync((service, token) => service.BrowseRemoteSharesAsync(editor, token), cancellationToken);

    public Task<RemoteShareMountResult> CreateRemoteShareMountAsync(
        RemoteShareMountEditor editor,
        CancellationToken cancellationToken = default) =>
        RunAsync((service, token) => service.CreateRemoteShareMountAsync(editor, token), cancellationToken);

    public Task<RemoteShareMountResult> UpdateManagedRemoteMountAsync(
        Guid id,
        RemoteShareMountEditor editor,
        CancellationToken cancellationToken = default) =>
        RunAsync((service, token) => service.UpdateManagedRemoteMountAsync(id, editor, token), cancellationToken);

    public Task DeleteManagedRemoteMountAsync(Guid id, CancellationToken cancellationToken = default) =>
        RunAsync(
            async (service, token) =>
            {
                await service.DeleteManagedRemoteMountAsync(id, token);
                return true;
            },
            cancellationToken);

    public Task<bool> DisconnectCurrentRemoteMountAsync(
        string localMountPath,
        CancellationToken cancellationToken = default) =>
        RunAsync((service, token) => service.DisconnectCurrentRemoteMountAsync(localMountPath, token), cancellationToken);

    public Task<bool> ReleaseTemporaryRemoteMountAsync(
        string localMountPath,
        CancellationToken cancellationToken = default) =>
        RunAsync((service, token) => service.ReleaseTemporaryRemoteMountAsync(localMountPath, token), cancellationToken);

    public Task<ShareToolingInstallResult> InstallMissingShareToolingAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync((service, token) => service.InstallMissingShareToolingAsync(token), cancellationToken);

    public Task<ShareToolingInstallResult> InstallMissingSshfsToolingAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync((service, token) => service.InstallMissingSshfsToolingAsync(token), cancellationToken);

    public Task<SshfsMountResult> CreateSshfsMountAsync(
        SshfsMountEditor editor,
        CancellationToken cancellationToken = default) =>
        RunAsync((service, token) => service.CreateSshfsMountAsync(editor, token), cancellationToken);

    public Task DeleteManagedSshfsMountAsync(Guid id, CancellationToken cancellationToken = default) =>
        RunAsync(
            async (service, token) =>
            {
                await service.DeleteManagedSshfsMountAsync(id, token);
                return true;
            },
            cancellationToken);

    private Task<T> RunAsync<T>(
        Func<IShareManagementService, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var shareService = scope.ServiceProvider.GetRequiredService<IShareManagementService>();
            return await operation(shareService, cancellationToken);
        }, cancellationToken);
    }
}
