// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayCloudflaredInstallResult(
    bool Success,
    bool BinaryInstalled,
    bool ServiceInstalled,
    bool ServiceRunning,
    string Summary);
