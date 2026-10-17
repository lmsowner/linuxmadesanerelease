// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record ExposedServiceConnectorDeploymentResult(
    bool Succeeded,
    string CommandText,
    int? ExitCode,
    string Summary,
    string StandardOutput,
    string StandardError);
