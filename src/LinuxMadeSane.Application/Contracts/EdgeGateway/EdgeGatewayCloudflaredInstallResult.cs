// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayCloudflaredInstallResult(
    bool Success,
    bool BinaryInstalled,
    bool ServiceInstalled,
    bool ServiceRunning,
    string Summary);
