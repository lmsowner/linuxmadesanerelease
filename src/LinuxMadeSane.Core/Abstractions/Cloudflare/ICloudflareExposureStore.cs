// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Core.Abstractions;

public interface ICloudflareExposureStore
{
    Task<CloudflareSettings?> GetSettingsAsync(Guid managedHostId, CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(CloudflareSettings settings, CancellationToken cancellationToken = default);

    Task DeleteSettingsAsync(Guid managedHostId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExposedServiceConfig>> ListConfigsAsync(Guid managedHostId, CancellationToken cancellationToken = default);

    Task<ExposedServiceConfig?> GetConfigAsync(Guid configId, CancellationToken cancellationToken = default);

    Task<ExposedServiceConfig?> GetConfigByHostnameAsync(
        Guid managedHostId,
        string hostname,
        CancellationToken cancellationToken = default);

    Task SaveConfigAsync(ExposedServiceConfig config, CancellationToken cancellationToken = default);

    Task DeleteConfigAsync(Guid configId, CancellationToken cancellationToken = default);
}
