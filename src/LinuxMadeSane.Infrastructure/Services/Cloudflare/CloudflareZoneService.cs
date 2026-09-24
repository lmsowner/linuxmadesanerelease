// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Infrastructure.Services.Cloudflare;

public sealed class CloudflareZoneService(ICloudflareClient client) : ICloudflareZoneService
{
    public async Task<IReadOnlyList<CloudflareAccount>> ListAccountsAsync(
        string apiToken,
        CancellationToken cancellationToken = default)
    {
        var results = await client.GetAllPagesAsync<CloudflareAccountDto>(
            apiToken,
            "accounts",
            cancellationToken: cancellationToken);

        return results
            .Select(item => item.ToModel())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<CloudflareZone>> ListZonesAsync(
        string apiToken,
        CancellationToken cancellationToken = default)
    {
        var results = await client.GetAllPagesAsync<CloudflareZoneDto>(
            apiToken,
            "zones",
            cancellationToken: cancellationToken);

        return results
            .Select(item => item.ToModel())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
