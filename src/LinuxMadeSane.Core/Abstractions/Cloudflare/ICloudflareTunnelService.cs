// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Core.Abstractions;

public interface ICloudflareTunnelService
{
    Task<IReadOnlyList<CloudflareTunnel>> ListTunnelsAsync(
        string apiToken,
        string accountId,
        CancellationToken cancellationToken = default);

    Task<CloudflareTunnel> CreateTunnelAsync(
        string apiToken,
        string accountId,
        string tunnelName,
        CancellationToken cancellationToken = default);

    Task<CloudflareTunnelConfiguration> GetConfigurationAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken = default);

    Task UpdateConfigurationAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CloudflareTunnelConfiguration configuration,
        CancellationToken cancellationToken = default);

    Task<string> GetTunnelTokenAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken = default);

    Task DeleteTunnelAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken = default);
}
