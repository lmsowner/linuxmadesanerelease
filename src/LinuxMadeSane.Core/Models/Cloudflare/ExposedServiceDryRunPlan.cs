// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record ExposedServiceDryRunPlan(
    string Hostname,
    string PublicUrl,
    string TunnelName,
    bool AccessEnabled,
    bool RequiresConfirmation,
    IReadOnlyList<ExposureWarning> Warnings,
    IReadOnlyList<ExposedServicePlanStep> Steps);
