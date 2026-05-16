// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Core.Abstractions;

public interface ICloudflareDnsService
{
    Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(
        string apiToken,
        string zoneId,
        CancellationToken cancellationToken = default);

    Task<CloudflareDnsRecord> CreateRecordAsync(
        string apiToken,
        string zoneId,
        CloudflareDnsRecord record,
        CancellationToken cancellationToken = default);

    Task<CloudflareDnsRecord> UpdateRecordAsync(
        string apiToken,
        string zoneId,
        CloudflareDnsRecord record,
        CancellationToken cancellationToken = default);

    Task DeleteRecordAsync(
        string apiToken,
        string zoneId,
        string recordId,
        CancellationToken cancellationToken = default);
}
