// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record ExposedServiceApplyResult(
    ExposedServiceConfig Config,
    string PublicUrl,
    string Status,
    string? NextStep,
    IReadOnlyList<ExposureWarning> Warnings,
    string? ConnectorInstallCommand,
    ExposedServiceConnectorDeploymentResult? ConnectorDeployment,
    string? ConnectorStatusCommand,
    string? ConnectorLogsCommand);
