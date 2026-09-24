// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record ExposedServiceDryRunPlan(
    string Hostname,
    string PublicUrl,
    string TunnelName,
    bool AccessEnabled,
    bool RequiresConfirmation,
    IReadOnlyList<ExposureWarning> Warnings,
    IReadOnlyList<ExposedServicePlanStep> Steps);
