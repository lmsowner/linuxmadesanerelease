// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Core.Abstractions;

public interface ICloudflareAccessService
{
    Task<IReadOnlyList<CloudflareAccessApplication>> ListApplicationsAsync(
        string apiToken,
        string accountId,
        CancellationToken cancellationToken = default);

    Task<CloudflareAccessApplication> CreateApplicationAsync(
        string apiToken,
        string accountId,
        CloudflareAccessApplication application,
        CancellationToken cancellationToken = default);

    Task<CloudflareAccessApplication> UpdateApplicationAsync(
        string apiToken,
        string accountId,
        CloudflareAccessApplication application,
        CancellationToken cancellationToken = default);

    Task DeleteApplicationAsync(
        string apiToken,
        string accountId,
        string applicationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudflareAccessPolicy>> ListPoliciesAsync(
        string apiToken,
        string accountId,
        string applicationId,
        CancellationToken cancellationToken = default);

    Task<CloudflareAccessPolicy> CreatePolicyAsync(
        string apiToken,
        string accountId,
        string applicationId,
        CloudflareAccessPolicy policy,
        CancellationToken cancellationToken = default);

    Task<CloudflareAccessPolicy> UpdatePolicyAsync(
        string apiToken,
        string accountId,
        string applicationId,
        CloudflareAccessPolicy policy,
        CancellationToken cancellationToken = default);

    Task DeletePolicyAsync(
        string apiToken,
        string accountId,
        string applicationId,
        string policyId,
        CancellationToken cancellationToken = default);
}
