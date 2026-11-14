// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Application.Contracts.Cloudflare;

public sealed record CloudflareExposedServiceListItemViewModel(
    ExposedServiceConfig Config,
    bool ExistsInLmsStore,
    bool ExistsInCloudflare,
    bool IsOutOfSync,
    string SyncSummary);
