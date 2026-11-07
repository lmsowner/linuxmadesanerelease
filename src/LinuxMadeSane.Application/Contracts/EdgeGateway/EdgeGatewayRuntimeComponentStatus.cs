// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayRuntimeComponentStatus(
    EdgeGatewayDiagnosticStatus Status,
    string Label,
    string Message);
