// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Cloudflare;
using Microsoft.Extensions.Options;

namespace LinuxMadeSane.Infrastructure.Services.Cloudflare;

public sealed class CloudflareDnsService(
    ICloudflareClient client,
    IOptions<CloudflareIntegrationOptions> options) : ICloudflareDnsService
{
    private readonly CloudflareIntegrationOptions integrationOptions = options.Value;

    public async Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(
        string apiToken,
        string zoneId,
        CancellationToken cancellationToken = default)
    {
        var results = await client.GetAllPagesAsync<CloudflareDnsRecordDto>(
            apiToken,
            $"zones/{zoneId}/dns_records",
            cancellationToken: cancellationToken);

        return results
            .Select(item => item.ToModel(zoneId))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<CloudflareDnsRecord> CreateRecordAsync(
        string apiToken,
        string zoneId,
        CloudflareDnsRecord record,
        CancellationToken cancellationToken = default)
    {
        var result = await client.PostAsync<IReadOnlyDictionary<string, object?>, CloudflareDnsRecordDto>(
            apiToken,
            $"zones/{zoneId}/dns_records",
            BuildRecordRequest(record),
            cancellationToken);

        return result.ToModel(zoneId);
    }

    public async Task<CloudflareDnsRecord> UpdateRecordAsync(
        string apiToken,
        string zoneId,
        CloudflareDnsRecord record,
        CancellationToken cancellationToken = default)
    {
        var result = await client.PatchAsync<IReadOnlyDictionary<string, object?>, CloudflareDnsRecordDto>(
            apiToken,
            $"zones/{zoneId}/dns_records/{record.Id}",
            BuildRecordRequest(record),
            cancellationToken);

        return result.ToModel(zoneId);
    }

    public Task DeleteRecordAsync(
        string apiToken,
        string zoneId,
        string recordId,
        CancellationToken cancellationToken = default) =>
        client.DeleteAsync(apiToken, $"zones/{zoneId}/dns_records/{recordId}", cancellationToken);

    private IReadOnlyDictionary<string, object?> BuildRecordRequest(CloudflareDnsRecord record)
    {
        var request = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = record.Type,
            ["name"] = record.Name,
            ["content"] = record.Content,
            ["ttl"] = record.Ttl,
            ["comment"] = string.IsNullOrWhiteSpace(record.Comment) ? integrationOptions.ManagedRecordComment : record.Comment
        };

        // Cloudflare rejects the proxied field itself for record types such as TXT,
        // even when its value is false. Only send it for record types Cloudflare can proxy.
        if (record.Type.Equals("A", StringComparison.OrdinalIgnoreCase) ||
            record.Type.Equals("AAAA", StringComparison.OrdinalIgnoreCase) ||
            record.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase))
        {
            request["proxied"] = record.Proxied;
        }

        return request;
    }
}
