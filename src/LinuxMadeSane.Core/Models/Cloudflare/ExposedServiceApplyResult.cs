// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
