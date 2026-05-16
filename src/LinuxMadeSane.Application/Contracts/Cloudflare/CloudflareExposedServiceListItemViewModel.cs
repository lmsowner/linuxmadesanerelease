// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Application.Contracts.Cloudflare;

public sealed record CloudflareExposedServiceListItemViewModel(
    ExposedServiceConfig Config,
    bool ExistsInLmsStore,
    bool ExistsInCloudflare,
    bool IsOutOfSync,
    string SyncSummary);
